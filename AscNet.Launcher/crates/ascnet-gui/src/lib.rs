//! Cross-platform AscNet launcher window (egui/eframe).
//!
//! The window only renders and dispatches: every slow operation (setup, verify, patch, server
//! start, game launch) runs on a worker thread from [`worker`].

pub mod settings;
mod views;
mod worker;

use anyhow::Result;
use ascnet_core::{
    download, install, local, package, steam, updater,
};
use ascnet_platform::{GameRuntime, LaunchOptions};
use settings::{DistributorConfig, Settings};
use std::{
    collections::VecDeque,
    path::PathBuf,
    sync::{
        atomic::{AtomicBool, AtomicU64, Ordering},
        mpsc::{self, Receiver, Sender},
        Arc,
    },
    time::Duration,
};
use worker::{Done, Event, ServerStatus, Snapshot};

pub fn run() -> Result<()> {
    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_inner_size([1020.0, 660.0])
            .with_min_inner_size([860.0, 560.0])
            .with_title("AscNet Launcher"),
        ..Default::default()
    };
    eframe::run_native(
        "AscNet Launcher",
        options,
        Box::new(|cc| Ok(Box::new(App::new(cc)))),
    )
    .map_err(|error| anyhow::anyhow!("{error}"))
}

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum View {
    Home,
    Client,
    Settings,
    Logs,
}

pub struct App {
    pub settings: Settings,
    pub config: Option<DistributorConfig>,
    pub build: Option<local::LocalBuild>,
    pub package: Option<package::PatchPackage>,
    pub patch: Option<install::PatchState>,
    pub server: Option<ServerStatus>,
    pub runtime: Option<GameRuntime>,
    pub client_detected: Option<download::Detected>,
    pub client_region: Option<download::Region>,
    pub game_running: bool,
    pub busy: bool,
    pub status: String,
    pub log: VecDeque<String>,
    pub view: View,
    pub update_available: bool,
    pub update_ready: Option<updater::StagedUpdate>,
    pub progress: Option<(u64, u64)>,
    pub progress_label: Option<String>,
    pub picker: Option<views::FolderPicker>,
    pub cancel: Arc<AtomicBool>,
    events: Receiver<Event>,
    sender: Sender<Event>,
    generation: Arc<AtomicU64>,
    active: u64,
    runtime_owner: Option<local::LocalRuntime>,
    last_game_poll: std::time::Instant,
}

impl App {
    fn new(cc: &eframe::CreationContext<'_>) -> Self {
        cc.egui_ctx.set_visuals(egui::Visuals::dark());
        let (sender, events) = mpsc::channel();
        let mut app = Self {
            settings: settings::load(),
            config: settings::distributor_config(),
            build: None,
            package: None,
            patch: None,
            server: None,
            runtime: None,
            client_detected: None,
            client_region: None,
            game_running: false,
            busy: false,
            status: "Ready".into(),
            log: VecDeque::new(),
            view: View::Home,
            update_available: false,
            update_ready: None,
            progress: None,
            progress_label: None,
            picker: None,
            cancel: Arc::new(AtomicBool::new(false)),
            events,
            sender,
            generation: Arc::new(AtomicU64::new(0)),
            active: 0,
            runtime_owner: None,
            last_game_poll: std::time::Instant::now(),
        };
        app.start_refresh();
        app
    }

    pub fn game(&self) -> Option<PathBuf> {
        self.settings
            .selected_game
            .clone()
            .filter(|path| steam::valid_game_directory(path))
    }

    pub fn push_log(&mut self, line: &str) {
        let line = line.trim().to_owned();
        if line.is_empty() {
            return;
        }
        if self.log.back() != Some(&line) {
            if self.log.len() >= 500 {
                self.log.pop_front();
            }
            self.log.push_back(line);
        }
    }

    fn start(&mut self, label: &str, work: impl FnOnce(&mut dyn FnMut(String)) -> Result<Done> + Send + 'static) {
        if self.busy {
            return;
        }
        self.busy = true;
        self.cancel.store(false, Ordering::SeqCst);
        self.progress = None;
        self.progress_label = None;
        self.status = label.to_owned();
        self.push_log(label);
        let sender = self.sender.clone();
        let generation = self.generation.fetch_add(1, Ordering::SeqCst) + 1;
        self.active = generation;
        std::thread::spawn(move || {
            let forward = sender.clone();
            let mut progress = move |line: String| {
                let _ = forward.send(Event::Progress(generation, line));
            };
            let result = work(&mut progress);
            let _ = sender.send(Event::Done(generation, result));
        });
    }

    pub fn start_refresh(&mut self) {
        let game = self.game();
        let config = self.config.clone();
        self.start("Checking the local installation…", move |progress| {
            progress("Reading build state and game files…".into());
            worker::refresh(&game, &config)
        });
    }

    pub fn start_setup(&mut self) {
        let (Some(game), Some(config)) = (self.game(), self.config.clone()) else {
            self.status = "Select a game folder and a distributor config first".into();
            return;
        };
        self.start("Preparing the local server build…", move |progress| {
            worker::prepare(config, game, progress)
        });
    }

    pub fn start_restore(&mut self) {
        let Some(game) = self.game() else { return };
        self.start("Restoring the retail client…", move |progress| {
            worker::restore(game, progress)
        });
    }

    pub fn start_play(&mut self) {
        let (Some(game), Some(build), Some(package)) =
            (self.game(), self.build.clone(), self.package.clone())
        else {
            self.status = "Run Setup first".into();
            return;
        };
        let options = LaunchOptions {
            origin: String::new(),
            fps: self.settings.fps_enabled.then_some(self.settings.fps_value),
            no_camera_fade: self.settings.no_camera_fade,
        };
        let runtime = self.settings.runtime;
        let cancel = self.cancel.clone();
        let sender = self.sender.clone();
        let generation = self.generation.fetch_add(1, Ordering::SeqCst) + 1;
        self.active = generation;
        self.busy = true;
        self.status = "Starting the local backend…".into();
        self.push_log("Starting the local backend…");
        let sender_progress = sender.clone();
        std::thread::spawn(move || {
            let mut progress = move |line: String| {
                let _ = sender_progress.send(Event::Progress(generation, line));
            };
            match worker::play(game, build, package, options, runtime, &mut progress, cancel) {
                Ok((runtime_owner, done)) => {
                    let _ = sender.send(Event::Runtime(Box::new(runtime_owner)));
                    let _ = sender.send(Event::Done(generation, Ok(done)));
                }
                Err(error) => {
                    let _ = sender.send(Event::Done(generation, Err(error)));
                }
            }
        });
    }

    pub fn start_client_job(&mut self, job: download::Job) {
        let Some(metadata) = settings::launcher_file("supported-client.json") else {
            self.status = "supported-client.json is missing beside the launcher".into();
            return;
        };
        let cancel = self.cancel.clone();
        let sender = self.sender.clone();
        let generation = self.generation.fetch_add(1, Ordering::SeqCst) + 1;
        self.active = generation;
        self.busy = true;
        self.status = "Working on the game client…".into();
        let sender_progress = sender.clone();
        std::thread::spawn(move || {
            let mut progress = move |line: String| {
                let _ = sender_progress.send(Event::Progress(generation, line));
            };
            let result = worker::client_job(metadata, job, cancel, &mut progress, sender.clone(), generation);
            let _ = sender.send(Event::Done(generation, result));
        });
    }

    pub fn start_client_scan(&mut self) {
        let Some(game) = self.settings.selected_game.clone() else {
            return;
        };
        if !game.is_dir() {
            return;
        }
        match download::detect(&game) {
            Ok(detected) => {
                self.client_region = detected.region;
                self.client_detected = Some(detected);
            }
            Err(error) => self.push_log(&format!("Could not read the game folder: {error}")),
        }
    }

    pub fn start_launcher_update(&mut self) {
        let Some(config) = self.config.clone() else { return };
        self.start("Checking for a launcher update…", move |_| {
            worker::check_launcher_update(&config)
        });
    }

    fn poll_events(&mut self, ctx: &egui::Context) {
        while let Ok(event) = self.events.try_recv() {
            match event {
                Event::Progress(generation, line) if generation == self.active => {
                    self.push_log(&line);
                    self.status = line;
                }
                Event::Client(generation, progress) if generation == self.active => {
                    self.progress = Some((progress.bytes_done, progress.bytes_total.max(1)));
                    self.progress_label = Some(format!(
                        "{:?} · {:.1}/{:.1} GiB",
                        progress.phase,
                        progress.bytes_done as f64 / 1e9,
                        progress.bytes_total as f64 / 1e9
                    ));
                }
                Event::Runtime(runtime) => {
                    if let Some(mut previous) = self.runtime_owner.replace(*runtime) {
                        let _ = previous.stop();
                    }
                }
                Event::Done(generation, result) if generation == self.active => {
                    self.busy = false;
                    self.progress = None;
                    self.progress_label = None;
                    match result {
                        Ok(Done::Refreshed(snapshot)) => {
                            self.apply_refresh(snapshot);
                            self.status = "Ready".into();
                        }
                        Ok(Done::Prepared(build)) => {
                            self.build = Some(build);
                            self.push_log("Local build prepared");
                            self.start_refresh();
                        }
                        Ok(Done::Restored) => {
                            self.patch = None;
                            self.push_log("Retail files restored");
                            self.start_refresh();
                        }
                        Ok(Done::Played { status, runtime }) => {
                            self.server = Some(status);
                            self.runtime = Some(runtime);
                            self.status = "Game launched".into();
                            self.push_log("Game launched");
                        }
                        Ok(Done::Client(outcome)) => {
                            self.push_log(&format!(
                                "Client job finished: {} downloaded, {} repaired, {} deleted",
                                outcome.downloaded_bytes, outcome.repaired, outcome.deleted
                            ));
                            self.start_client_scan();
                            self.start_refresh();
                        }
                        Ok(Done::Launcher(staged)) => match staged {
                            Some(staged) => {
                                self.push_log(&format!("Launcher {} is ready to install", staged.version()));
                                self.update_ready = Some(staged);
                                self.update_available = true;
                                self.status = "Launcher update ready; apply it from the footer".into();
                            }
                            None => self.push_log("Launcher is up to date"),
                        },
                        Err(error) => {
                            let message = format!("{error:#}");
                            self.status = message.lines().next().unwrap_or("Operation failed").to_owned();
                            self.push_log(&message);
                        }
                    }
                }
                _ => {}
            }
        }
        if self.busy {
            ctx.request_repaint_after(Duration::from_millis(120));
        }
    }

    fn apply_refresh(&mut self, snapshot: Snapshot) {
        self.update_available = snapshot.update.unwrap_or(false) || self.update_available;
        self.build = snapshot.build;
        self.package = snapshot.package;
        self.patch = snapshot.patch;
        if self.runtime_owner.is_none() {
            self.server = None;
        }
    }

    fn poll_game(&mut self) {
        if self.last_game_poll.elapsed() < Duration::from_millis(1500) {
            return;
        }
        self.last_game_poll = std::time::Instant::now();
        self.game_running = install::game_running().unwrap_or(true);
    }

    pub fn apply_update(&mut self) {
        let Some(staged) = self.update_ready.take() else { return };
        match updater::launch_update(staged) {
            Ok(()) => {
                self.push_log("Launcher update applied; restart the launcher");
                self.status = "Update applied; restart the launcher".into();
            }
            Err(error) => self.push_log(&format!("Update failed: {error:#}")),
        }
    }
}

impl eframe::App for App {
    fn ui(&mut self, ui: &mut egui::Ui, _frame: &mut eframe::Frame) {
        let ctx = ui.ctx().clone();
        self.poll_events(&ctx);
        self.poll_game();
        if self.picker.is_none() {
            views::top_bar(self, ui);
            views::side_bar(self, ui);
            egui::CentralPanel::default().show(ui, |ui| match self.view {
                View::Home => views::home(self, ui),
                View::Client => views::client(self, ui),
                View::Settings => views::settings(self, ui),
                View::Logs => views::logs(self, ui),
            });
            views::footer(self, ui);
        }
        views::folder_dialog(self, ui);
    }

    fn on_exit(&mut self, _gl: Option<&eframe::glow::Context>) {
        if let Some(mut runtime) = self.runtime_owner.take() {
            let _ = runtime.stop();
        }
        let _ = settings::save(&self.settings);
        let _ = updater::acknowledge_startup();
    }
}
