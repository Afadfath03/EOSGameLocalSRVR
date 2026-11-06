//! Linux implementation of the OS boundary.
//!
//! The retail client is a Windows binary, so the launcher manages a Wine prefix (or adopts a
//! Steam Proton prefix) and keeps the server runtime native.

use anyhow::{bail, ensure, Context, Result};
use std::{
    fs,
    os::unix::process::CommandExt,
    path::{Path, PathBuf},
    process::{Child, Command},
    sync::Mutex,
    time::Duration,
};

use crate::{GameRuntime, LaunchOptions, SteamInstall};

const DOCUMENT: &str = "PGR_Data/StreamingAssets/document";

pub fn data_root() -> Result<PathBuf> {
    if let Some(value) = std::env::var_os("XDG_DATA_HOME") {
        let path = PathBuf::from(value);
        if path.is_absolute() {
            return Ok(path);
        }
    }
    let home = std::env::var_os("HOME").context("HOME is not set")?;
    Ok(PathBuf::from(home).join(".local/share"))
}

pub fn launcher_install_dir() -> Result<PathBuf> {
    // Inside an AppImage current_exe() points at the read-only mount; the update target is the file
    // the user launched.
    if let Some(appimage) = std::env::var_os("APPIMAGE") {
        let path = PathBuf::from(appimage);
        if path.is_absolute() {
            return path
                .parent()
                .map(Path::to_path_buf)
                .context("APPIMAGE has no parent directory");
        }
    }
    let exe = std::env::current_exe()?;
    exe.parent()
        .map(Path::to_path_buf)
        .context("launcher has no directory")
}

pub fn launcher_asset_name() -> &'static str {
    "AscNetLauncher-x86_64.AppImage"
}

pub fn executable_suffix() -> &'static str {
    ""
}

pub fn release_asset_name() -> &'static str {
    launcher_asset_name()
}

// ───────────────────────── document access ─────────────────────────

fn writable(path: &Path) -> bool {
    use std::os::unix::ffi::OsStrExt;
    let Ok(c) = std::ffi::CString::new(path.as_os_str().as_bytes()) else {
        return false;
    };
    unsafe { libc::access(c.as_ptr(), libc::W_OK) == 0 }
}

pub fn document_writable(client: &Path) -> Result<bool> {
    let document = client.join(DOCUMENT);
    let target = if document.exists() {
        document
    } else {
        client.to_path_buf()
    };
    Ok(writable(&target))
}

/// Wine runs as this user, so there is no ACL to grant; the directory only needs to exist.
pub fn prepare_document_for_caller(client: &Path, _pid: u32, _created: u64) -> Result<()> {
    let document = client.join(DOCUMENT);
    match fs::create_dir_all(&document) {
        Ok(()) => Ok(()),
        Err(error) => Err(error).context("creating persistent game document directory"),
    }
}

pub fn caller_identity() -> Result<(u32, u64)> {
    Ok((std::process::id(), 0))
}

// ───────────────────────── process discovery and control ─────────────────────────

pub fn running_under() -> Result<bool> {
    Ok(processes().any(|cmdline| cmdline.contains("PGR.exe")))
}

fn processes() -> impl Iterator<Item = String> {
    let mut result = Vec::new();
    let Ok(entries) = fs::read_dir("/proc") else {
        return result.into_iter();
    };
    for entry in entries.flatten() {
        let name = entry.file_name();
        let name = name.to_string_lossy();
        if !name.bytes().all(|b| b.is_ascii_digit()) {
            continue;
        }
        if let Ok(bytes) = fs::read(entry.path().join("cmdline")) {
            result.push(String::from_utf8_lossy(&bytes).replace('\0', " "));
        }
    }
    result.into_iter()
}

/// Every spawned child becomes its own process group, so stopping the runtime reaches the whole tree.
pub struct ProcessGuard {
    groups: Mutex<Vec<i32>>,
}

impl ProcessGuard {
    pub fn new() -> Result<Self> {
        Ok(Self {
            groups: Mutex::new(Vec::new()),
        })
    }

    pub fn assign(&self, child: &Child) -> Result<()> {
        self.groups.lock().unwrap().push(child.id() as i32);
        Ok(())
    }
}

impl Drop for ProcessGuard {
    fn drop(&mut self) {
        for group in self.groups.lock().unwrap().iter().rev() {
            unsafe {
                libc::kill(-*group, libc::SIGTERM);
            }
        }
    }
}

/// Puts the child in its own process group before it starts.
pub fn isolate_process_group(command: &mut Command) {
    command.process_group(0);
}

pub fn hide_console(command: &mut Command) -> &mut Command {
    // No console windows on Linux.
    command
}

pub fn terminate_tree(child: &mut Child) {
    let group = child.id() as i32;
    unsafe {
        libc::kill(-group, libc::SIGTERM);
    }
    let deadline = std::time::Instant::now() + Duration::from_secs(5);
    while std::time::Instant::now() < deadline {
        if child.try_wait().ok().flatten().is_some() {
            return;
        }
        std::thread::sleep(Duration::from_millis(50));
    }
    unsafe {
        libc::kill(-group, libc::SIGKILL);
    }
    let _ = child.wait();
}

pub fn free_space(path: &Path) -> Option<u64> {
    use std::os::unix::ffi::OsStrExt;
    let c = std::ffi::CString::new(path.as_os_str().as_bytes()).ok()?;
    let mut stat: libc::statvfs = unsafe { std::mem::zeroed() };
    if unsafe { libc::statvfs(c.as_ptr(), &mut stat) } != 0 {
        return None;
    }
    Some((stat.f_bavail as u64).saturating_mul(stat.f_frsize as u64))
}

/// Holds an exclusive lock on `path` for the lifetime of the returned file.
pub fn exclusive_lock(path: &Path) -> Result<std::fs::File> {
    use std::os::unix::io::AsRawFd;
    let file = std::fs::OpenOptions::new()
        .read(true)
        .write(true)
        .create(true)
        .truncate(false)
        .open(path)
        .context("another local setup is already active")?;
    let locked = unsafe { libc::flock(file.as_raw_fd(), libc::LOCK_EX | libc::LOCK_NB) };
    if locked != 0 {
        bail!("another local setup is already active");
    }
    Ok(file)
}

pub fn is_reparse(path: &Path) -> Result<bool> {
    Ok(fs::symlink_metadata(path)?.file_type().is_symlink())
}

/// Link test that works from metadata already obtained for the same path.
pub fn metadata_is_link(metadata: &fs::Metadata) -> bool {
    metadata.file_type().is_symlink()
}

pub fn atomic_replace(source: &Path, destination: &Path) -> Result<()> {
    if destination.exists() {
        // Same-directory rename is atomic on Linux.
        fs::rename(source, destination)?;
    } else {
        fs::rename(source, destination)?;
    }
    Ok(())
}

// ───────────────────────── game launch ─────────────────────────

pub fn launch_game(client: &Path, options: &LaunchOptions, runtime: &GameRuntime) -> Result<()> {
    let mut command = match runtime {
        GameRuntime::Native => bail!("the Windows client cannot run natively on Linux"),
        GameRuntime::ManagedWine { prefix, wine } => {
            ensure!(wine.is_file(), "Wine runtime is missing: {}", wine.display());
            let mut command = Command::new(wine);
            command
                .arg("PGR.exe")
                .current_dir(client)
                .env("WINEPREFIX", prefix)
                .env("WINEDEBUG", "-all")
                .env("WINEDLLOVERRIDES", "d3d11,dxgi=n,b;version=n,b")
                .env("WINEDLLPATH", wine_dir(wine));
            command
        }
        GameRuntime::Proton {
            prefix,
            proton,
            steam_root,
            ..
        } => {
            ensure!(proton.is_file(), "Proton entry point is missing: {}", proton.display());
            let mut command = Command::new(proton);
            command
                .arg("waitforexitandrun")
                .arg(client.join("PGR.exe"))
                .current_dir(client)
                .env("STEAM_COMPAT_DATA_PATH", prefix)
                .env("STEAM_COMPAT_CLIENT_INSTALL_PATH", steam_root)
                .env("WINEDEBUG", "-all")
                .env("WINEDLLOVERRIDES", "d3d11,dxgi=n,b;version=n,b");
            command
        }
    };
    command.env("ASCNET_PATCH_ORIGIN", &options.origin);
    if options.no_camera_fade {
        command.env("ASCNET_PATCH_NOFADE", "1");
    }
    match options.fps {
        Some(fps) => command.env("ASCNET_PATCH_FPS", fps.to_string()),
        None => command.env_remove("ASCNET_PATCH_FPS"),
    };
    for key in [
        "ASCNET_PATCH_TRACE",
        "ASCNET_PATCH_PROBE",
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "ALL_PROXY",
        "NO_PROXY",
        "http_proxy",
        "https_proxy",
        "all_proxy",
        "no_proxy",
    ] {
        command.env_remove(key);
    }
    isolate_process_group(&mut command);
    command.spawn().context("launching PGR.exe through Wine")?;
    Ok(())
}

fn wine_dir(wine: &Path) -> PathBuf {
    wine.parent().map(Path::to_path_buf).unwrap_or_default()
}

// ───────────────────────── Steam discovery ─────────────────────────

pub fn steam_roots() -> Vec<PathBuf> {
    let mut roots = Vec::new();
    if let Some(path) = std::env::var_os("STEAM_PATH") {
        roots.push(path.into());
    }
    if let Some(home) = std::env::var_os("HOME") {
        let home = PathBuf::from(home);
        roots.push(home.join(".steam/steam"));
        roots.push(home.join(".local/share/Steam"));
        roots.push(home.join(".var/app/com.valvesoftware.Steam/data/Steam"));
    }
    roots.retain(|root| root.is_dir());
    roots.sort();
    roots.dedup();
    roots
}

pub fn steam_installs() -> Vec<SteamInstall> {
    let mut installs = Vec::new();
    for root in steam_roots() {
        for library in super::steam_libraries(&root) {
            let apps = library.join("steamapps");
            let Ok(entries) = fs::read_dir(&apps) else {
                continue;
            };
            for entry in entries.flatten() {
                let name = entry.file_name();
                let name = name.to_string_lossy();
                if !name.starts_with("appmanifest_") || !name.ends_with(".acf") {
                    continue;
                }
                let Ok(text) = fs::read_to_string(entry.path()) else {
                    continue;
                };
                let Ok(fields) = super::vdf_object_fields(&text, "AppState") else {
                    continue;
                };
                let Some(dir) = fields.get("installdir") else {
                    continue;
                };
                let game = apps.join("common").join(dir);
                if !game.join("PGR.exe").is_file() {
                    continue;
                }
                let app_id = name
                    .trim_start_matches("appmanifest_")
                    .trim_end_matches(".acf")
                    .to_owned();
                if let Some(runtime) = proton_runtime(&root, &apps, &app_id) {
                    installs.push(SteamInstall {
                        game,
                        app_id: Some(app_id),
                        runtime,
                    });
                }
            }
        }
    }
    installs
}

/// Proton prefixes live in `steamapps/compatdata/<appid>/pfx`; the tool that created them is either
/// a Steam-provided Proton or a compatibility-tool install.
fn proton_runtime(steam_root: &Path, apps: &Path, app_id: &str) -> Option<GameRuntime> {
    let prefix = apps.join("compatdata").join(app_id).join("pfx");
    if !prefix.is_dir() {
        return None;
    }
    let proton = proton_candidates(steam_root).into_iter().next()?;
    Some(GameRuntime::Proton {
        prefix,
        proton,
        app_id: app_id.to_owned(),
        steam_root: steam_root.to_path_buf(),
    })
}

pub fn proton_candidates(steam_root: &Path) -> Vec<PathBuf> {
    let mut found = Vec::new();
    let compatibilitytools = steam_root.join("compatibilitytools.d");
    if let Ok(entries) = fs::read_dir(&compatibilitytools) {
        for entry in entries.flatten() {
            let proton = entry.path().join("proton");
            if proton.is_file() {
                found.push(proton);
            }
        }
    }
    let common = steam_root.join("steamapps/common");
    if let Ok(entries) = fs::read_dir(&common) {
        for entry in entries.flatten() {
            let name = entry.file_name().to_string_lossy().to_lowercase();
            if name.starts_with("proton") {
                let proton = entry.path().join("proton");
                if proton.is_file() {
                    found.push(proton);
                }
            }
        }
    }
    found.sort();
    found.dedup();
    found
}

pub fn wine_candidates() -> Vec<PathBuf> {
    let mut found = Vec::new();
    if let Some(path) = std::env::var_os("ASCNET_WINE") {
        let path = PathBuf::from(path);
        if path.is_file() {
            found.push(path);
        }
    }
    if let Some(path) = std::env::var_os("PATH") {
        for name in ["wine", "wine64", "wine-stable", "wine-staging"] {
            for directory in std::env::split_paths(&path) {
                let candidate = directory.join(name);
                if candidate.is_file() {
                    found.push(candidate);
                }
            }
        }
    }
    if let Ok(root) = crate::launcher_data_root() {
        let managed = root.join("runtime/wine/bin/wine");
        if managed.is_file() {
            found.push(managed);
        }
    }
    found.sort();
    found.dedup();
    found
}

/// Pinned portable Wine/DXVK runtime. Both fields must be filled by a release; empty values mean
/// "no downloadable runtime is published for this build", never "skip verification".
#[derive(serde::Deserialize)]
pub struct ManagedWineManifest {
    pub url: String,
    pub sha256: String,
}

/// Creates the launcher-owned prefix on first use and returns the runtime to launch with.
pub fn ensure_managed_wine(progress: &mut dyn FnMut(&str)) -> Result<GameRuntime> {
    let wine = wine_candidates()
        .into_iter()
        .next()
        .context("Wine is not installed; install your distribution's wine package or set ASCNET_WINE")?;
    let root = crate::launcher_data_root()?;
    let prefix = root.join("wine/ascnet");
    if !prefix.join("system.reg").is_file() {
        progress("Initializing the Wine prefix (first run)…");
        fs::create_dir_all(&prefix)?;
        let status = Command::new(&wine)
            .arg("wineboot")
            .arg("-i")
            .env("WINEPREFIX", &prefix)
            .env("WINEDEBUG", "-all")
            .status()
            .context("initializing the Wine prefix")?;
        ensure!(status.success(), "wineboot failed with {status}");
    }
    Ok(GameRuntime::ManagedWine { prefix, wine })
}



#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn metadata_link_detection_matches_symlinks() {
        let root = std::env::temp_dir().join(format!("ascnet-link-{}", uuid_like()));
        std::fs::create_dir_all(&root).unwrap();
        let target = root.join("target");
        std::fs::write(&target, b"x").unwrap();
        let link = root.join("link");
        std::os::unix::fs::symlink(&target, &link).unwrap();
        assert!(metadata_is_link(&std::fs::symlink_metadata(&link).unwrap()));
        assert!(!metadata_is_link(&std::fs::symlink_metadata(&target).unwrap()));
        assert!(is_reparse(&link).unwrap());
        assert!(!is_reparse(&target).unwrap());
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn free_space_reports_a_positive_amount() {
        let free = free_space(&std::env::temp_dir()).expect("statvfs failed");
        assert!(free > 0);
    }

    #[test]
    fn game_process_scan_does_not_error() {
        // `/proc` may be restricted in a sandbox; the scan must not treat that as "running".
        assert!(!running_under().unwrap_or(false));
    }

    fn uuid_like() -> String {
        format!(
            "{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        )
    }
}
