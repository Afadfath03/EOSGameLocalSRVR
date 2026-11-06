//! Background work. Every long action runs on its own thread and reports progress through a
//! channel; the UI thread only renders and never blocks on disk or network.

use crate::settings::DistributorConfig;
use ascnet_core::{
    download, install, local, package, steam, updater,
};
use ascnet_platform::{GameRuntime, LaunchOptions};
use std::{
    path::PathBuf,
    sync::{
        atomic::AtomicBool,
        mpsc::Sender,
        Arc,
    },
};

pub enum Event {
    Progress(u64, String),
    Client(u64, download::Progress),
    /// Hands the live backend owned by a finished play job to the window.
    Runtime(Box<local::LocalRuntime>),
    Done(u64, anyhow::Result<Done>),
}

pub struct Snapshot {
    pub build: Option<local::LocalBuild>,
    pub package: Option<package::PatchPackage>,
    pub patch: Option<install::PatchState>,
    pub update: Option<bool>,
}

pub enum Done {
    Refreshed(Snapshot),
    Prepared(local::LocalBuild),
    Restored,
    Played { status: ServerStatus, runtime: GameRuntime },
    Client(download::Outcome),
    Launcher(Option<updater::StagedUpdate>),
}

#[derive(Clone, Debug, serde::Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ServerStatus {
    pub schema_version: u32,
    pub server_version: String,
    pub online: bool,
    pub maintenance: bool,
    pub message: String,
    pub minimum_patch_version: Option<String>,
    pub minimum_launcher_version: Option<String>,
}

pub fn refresh(game: &Option<PathBuf>, config: &Option<DistributorConfig>) -> anyhow::Result<Done> {
    let build = local::load_build()?;
    let (package, patch) = match (&build, game) {
        (Some(build), Some(game)) => {
            let package = package::load_package(&build.patch_directory)?;
            let patch = install::inspect(game, &package)?;
            (Some(package), Some(patch))
        }
        _ => (None, None),
    };
    let update = match (config, &build) {
        (Some(config), Some(build)) => {
            local::check_update(&config.repository_url, &config.branch, Some(build)).unwrap_or(None)
        }
        _ => None,
    };
    Ok(Done::Refreshed(Snapshot {
        build,
        package,
        patch,
        update,
    }))
}

pub fn prepare(config: DistributorConfig, game: PathBuf, mut progress: impl FnMut(String)) -> anyhow::Result<Done> {
    let build = local::prepare(&config.repository_url, &config.branch, &game, &mut |line: &str| {
        progress(line.to_owned())
    })?;
    Ok(Done::Prepared(build))
}

pub fn restore(game: PathBuf, mut progress: impl FnMut(String)) -> anyhow::Result<Done> {
    install::restore_with_consent(&game, &mut |line| progress(line))?;
    Ok(Done::Restored)
}

pub fn play(
    game: PathBuf,
    build: local::LocalBuild,
    package: package::PatchPackage,
    options: LaunchOptions,
    runtime_choice: crate::settings::RuntimeChoice,
    mut progress: impl FnMut(String),
    cancel: Arc<AtomicBool>,
) -> anyhow::Result<(local::LocalRuntime, Done)> {
    if install::inspect(&game, &package)? != install::PatchState::Current {
        anyhow::bail!("Run Setup / Update to install the current local patch");
    }
    install::install_with_consent(&game, &package, &mut |line| progress(line))?;
    let runtime = resolve_runtime(&game, runtime_choice, &mut progress)?;
    let mut local = local::LocalRuntime::start(&build, &mut |line: &str| progress(line.to_owned()))?;
    let origin = format!("http://127.0.0.1:{}", build.sdk_port);
    let status = match fetch_status(&origin) {
        Ok(status) => status,
        Err(error) => {
            let _ = local.stop();
            return Err(error);
        }
    };
    if let Err(error) = install::launch(&game, &origin, options.no_camera_fade, options.fps, &runtime) {
        let _ = local.stop();
        return Err(error);
    }
    let _ = cancel;
    Ok((
        local,
        Done::Played {
            status,
            runtime,
        },
    ))
}

fn resolve_runtime(
    game: &std::path::Path,
    choice: crate::settings::RuntimeChoice,
    progress: &mut dyn FnMut(String),
) -> anyhow::Result<GameRuntime> {
    if cfg!(windows) {
        return Ok(GameRuntime::Native);
    }
    if choice != crate::settings::RuntimeChoice::Wine {
        if let Some(runtime) = steam::runtime_for(game) {
            if choice != crate::settings::RuntimeChoice::Proton
                || matches!(runtime, GameRuntime::Proton { .. })
            {
                progress(match runtime {
                    GameRuntime::Proton { .. } => "Using the Steam Proton prefix".into(),
                    _ => "Using the managed Wine prefix".into(),
                });
                return Ok(runtime);
            }
        }
    }
    ascnet_platform::ensure_managed_wine(&mut |line: &str| progress(line.to_owned()))
}

pub fn fetch_status(origin: &str) -> anyhow::Result<ServerStatus> {
    let status = reqwest::blocking::Client::builder()
        .no_proxy()
        .timeout(std::time::Duration::from_secs(3))
        .build()?
        .get(format!("{origin}/api/launcher/status"))
        .send()?
        .error_for_status()?
        .json::<ServerStatus>()?;
    anyhow::ensure!(status.schema_version == 1, "unsupported server status schema {}", status.schema_version);
    Ok(status)
}

pub fn client_job(
    metadata: PathBuf,
    job: download::Job,
    cancel: Arc<AtomicBool>,
    mut progress: impl FnMut(String),
    sender: Sender<Event>,
    generation: u64,
) -> anyhow::Result<Done> {
    let sources = download::sources(&metadata)?;
    let detected = download::detect(job.dir())?;
    let source = match detected.region {
        Some(region) => sources
            .into_iter()
            .find(|source| source.region() == region)
            .ok_or_else(|| anyhow::anyhow!("no pinned source for the detected region"))?,
        None => sources
            .into_iter()
            .next()
            .ok_or_else(|| anyhow::anyhow!("supported-client.json has no sources"))?,
    };
    let plan = download::plan(&source, job)?;
    progress(format!(
        "{} file(s), {:.1} GiB download",
        plan.files,
        plan.download_bytes as f64 / 1e9
    ));
    let outcome = download::run(plan, &cancel, &mut |update| {
        let _ = sender.send(Event::Client(generation, update));
    })?;
    Ok(Done::Client(outcome))
}

pub fn check_launcher_update(config: &DistributorConfig) -> anyhow::Result<Done> {
    let release = updater::check(&config.repository_url, env!("CARGO_PKG_VERSION"))?;
    Ok(Done::Launcher(match release {
        Some(release) => Some(updater::stage(&release)?),
        None => None,
    }))
}
