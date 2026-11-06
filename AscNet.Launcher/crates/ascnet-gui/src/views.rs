//! Window layout. Rendering only; all actions are dispatched to the app's worker threads.

use crate::{App, View};
use ascnet_core::{download, install};
use ascnet_platform::launcher_asset_name;
use std::path::{Path, PathBuf};

pub struct FolderPicker {
    current: PathBuf,
    entries: Vec<PathBuf>,
}

impl FolderPicker {
    pub fn new(start: Option<&Path>) -> Self {
        let current = start
            .filter(|path| path.is_dir())
            .map(Path::to_path_buf)
            .or_else(|| std::env::var_os("HOME").map(PathBuf::from))
            .filter(|path| path.is_dir())
            .unwrap_or_else(|| PathBuf::from("/"));
        let mut picker = Self {
            current,
            entries: Vec::new(),
        };
        picker.reload();
        picker
    }

    fn reload(&mut self) {
        self.entries.clear();
        if let Ok(entries) = std::fs::read_dir(&self.current) {
            for entry in entries.flatten() {
                let path = entry.path();
                if path.is_dir() {
                    self.entries.push(path);
                }
            }
        }
        self.entries.sort();
    }
}

pub fn top_bar(app: &App, ui: &mut egui::Ui) {
    egui::Panel::top("top").show(ui, |ui| {
        ui.horizontal(|ui| {
            ui.heading("AscNet");
            ui.label(egui::RichText::new(format!("Launcher {}", env!("CARGO_PKG_VERSION"))).weak());
            ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                if app.busy {
                    ui.spinner();
                }
                if app.game_running {
                    ui.colored_label(egui::Color32::LIGHT_GREEN, "game running");
                }
                if app.update_available {
                    if ui.button("Update launcher").clicked() {
                        // Handled by the footer's apply button; keep the hint visible here.
                    }
                }
            });
        });
    });
}

pub fn side_bar(app: &mut App, ui: &mut egui::Ui) {
    egui::Panel::left("nav")
        .resizable(false)
        .exact_size(160.0)
        .show(ui, |ui| {
            ui.add_space(8.0);
            for (view, label) in [
                (View::Home, "Home"),
                (View::Client, "Get game"),
                (View::Settings, "Settings"),
                (View::Logs, "Logs"),
            ] {
                if ui
                    .add_sized([140.0, 30.0], egui::Button::selectable(app.view == view, label))
                    .clicked()
                {
                    app.view = view;
                    if view == View::Client {
                        app.start_client_scan();
                    }
                }
                ui.add_space(2.0);
            }
            ui.with_layout(egui::Layout::bottom_up(egui::Align::LEFT), |ui| {
                ui.label(egui::RichText::new(launcher_asset_name()).small().weak());
                if let Some(runtime) = &app.runtime {
                    ui.label(egui::RichText::new(format!("{runtime:?}")).small().weak());
                }
            });
        });
}

pub fn footer(app: &mut App, ui: &mut egui::Ui) {
    egui::Panel::bottom("footer").show(ui, |ui| {
        ui.add_space(4.0);
        ui.horizontal(|ui| {
            ui.label(&app.status);
            if let Some((done, total)) = app.progress {
                ui.add(
                    egui::ProgressBar::new((done as f32 / total as f32).clamp(0.0, 1.0))
                        .desired_width(260.0)
                        .show_percentage(),
                );
            }
        });
        if let Some(label) = &app.progress_label {
            ui.label(egui::RichText::new(label).small().weak());
        }
        if app.busy {
            if ui.button("Cancel").clicked() {
                app.cancel.store(true, std::sync::atomic::Ordering::SeqCst);
                app.push_log("Cancellation requested");
            }
        }
        if app.update_ready.is_some() && ui.button("Apply launcher update").clicked() {
            app.apply_update();
        }
        ui.add_space(4.0);
    });
}

pub fn home(app: &mut App, ui: &mut egui::Ui) {
    ui.heading("Home");
    ui.add_space(8.0);
    game_row(app, ui, "Game folder");
    ui.add_space(10.0);
    egui::Grid::new("home-status")
        .num_columns(2)
        .spacing([18.0, 6.0])
        .show(ui, |ui| {
            ui.label("Local build");
            match &app.build {
                Some(build) => ui.label(format!("{} ({})", &build.revision[..12.min(build.revision.len())], build.repository)),
                None => ui.label("not prepared"),
            };
            ui.end_row();
            ui.label("Patch state");
            ui.label(patch_label(app.patch.as_ref()));
            ui.end_row();
            ui.label("Game process");
            ui.label(if app.game_running { "running" } else { "stopped" });
            ui.end_row();
            ui.label("Backend");
            match &app.server {
                Some(status) => ui.label(format!("{} — {}", status.server_version, status.message)),
                None => ui.label("stopped"),
            };
            ui.end_row();
        });
    ui.add_space(12.0);
    ui.horizontal(|ui| {
        if ui.add_enabled(!app.busy, egui::Button::new("SETUP")).clicked() {
            app.start_setup();
        }
        if ui.add_enabled(!app.busy, egui::Button::new("RESTORE")).clicked() {
            app.start_restore();
        }
        let can_play = !app.busy
            && !app.game_running
            && app.build.is_some()
            && app.package.is_some()
            && matches!(app.patch, Some(install::PatchState::Current));
        if ui.add_enabled(can_play, egui::Button::new("PLAY")).clicked() {
            app.start_play();
        }
        if ui.add_enabled(!app.busy, egui::Button::new("Refresh")).clicked() {
            app.start_refresh();
        }
    });
    ui.add_space(6.0);
    if let Some(config) = &app.config {
        ui.label(
            egui::RichText::new(format!("Source: {} @ {}", config.repository_url, config.branch))
                .small()
                .weak(),
        );
    } else {
        ui.colored_label(
            egui::Color32::YELLOW,
            "launcher.json is missing: Setup cannot build the server",
        );
    }
}

fn game_row(app: &mut App, ui: &mut egui::Ui, label: &str) {
    ui.horizontal(|ui| {
        ui.label(label);
        let mut text = app
            .settings
            .selected_game
            .as_ref()
            .map(|path| path.display().to_string())
            .unwrap_or_default();
        let response = ui.add_sized([460.0, 24.0], egui::TextEdit::singleline(&mut text));
        if response.changed() {
            app.settings.selected_game = (!text.trim().is_empty()).then(|| PathBuf::from(text.trim()));
        }
        if ui.button("Browse…").clicked() {
            app.picker = Some(FolderPicker::new(app.settings.selected_game.as_deref()));
        }
        if ui.button("Detect").clicked() {
            if let Ok(Some(found)) = ascnet_core::steam::discover_game() {
                app.settings.selected_game = Some(found);
            }
        }
    });
}

pub fn client(app: &mut App, ui: &mut egui::Ui) {
    ui.heading("Get game");
    ui.label(
        egui::RichText::new(
            "Each language is a separate Kuro client. Download, verify, update, or adopt an existing folder.",
        )
        .small()
        .weak(),
    );
    ui.add_space(8.0);
    ui.horizontal(|ui| {
        ui.label("Language");
        let current = app
            .client_region
            .map(download::Region::label)
            .unwrap_or("auto");
        egui::ComboBox::from_id_salt("client-region")
            .selected_text(current)
            .show_ui(ui, |ui| {
                for region in download::Region::ALL {
                    if ui
                        .selectable_label(app.client_region == Some(region), region.label())
                        .clicked()
                    {
                        app.client_region = Some(region);
                    }
                }
            });
        if ui.button("Rescan").clicked() {
            app.start_client_scan();
        }
    });
    game_row(app, ui, "Install folder");
    ui.add_space(8.0);
    if let Some(detected) = &app.client_detected {
        egui::Grid::new("client-detected")
            .num_columns(2)
            .spacing([18.0, 6.0])
            .show(ui, |ui| {
                ui.label("Edition");
                ui.label(detected.region.map(download::Region::label).unwrap_or("unknown"));
                ui.end_row();
                ui.label("Version");
                ui.label(detected.version.clone().unwrap_or_else(|| "unknown".into()));
                ui.end_row();
                ui.label("Steam copy");
                ui.label(if detected.steam { "yes" } else { "no" });
                ui.end_row();
                ui.label("AscNet patch");
                ui.label(if detected.launcher_patched { "installed" } else { "not installed" });
                ui.end_row();
            });
    } else {
        ui.label("No game folder scanned yet.");
    }
    ui.add_space(12.0);
    let Some(dir) = app.settings.selected_game.clone() else {
        return;
    };
    ui.horizontal(|ui| {
        if ui.add_enabled(!app.busy, egui::Button::new("DOWNLOAD")).clicked() {
            app.start_client_job(download::Job::Install { dir: dir.clone() });
        }
        if ui.add_enabled(!app.busy, egui::Button::new("VERIFY & REPAIR")).clicked() {
            app.start_client_job(download::Job::Repair { dir: dir.clone() });
        }
        if ui.add_enabled(!app.busy, egui::Button::new("ADOPT")).clicked() {
            app.start_client_job(download::Job::Adopt { dir: dir.clone() });
        }
        if ui.add_enabled(!app.busy, egui::Button::new("UPDATE")).clicked() {
            app.start_client_job(download::Job::Update { dir: dir.clone() });
        }
    });
}

pub fn settings(app: &mut App, ui: &mut egui::Ui) {
    ui.heading("Settings");
    ui.add_space(8.0);
    ui.checkbox(&mut app.settings.fps_enabled, "Cap the frame rate");
    ui.horizontal(|ui| {
        ui.add_enabled(
            app.settings.fps_enabled,
            egui::DragValue::new(&mut app.settings.fps_value).range(15..=480),
        );
        ui.label("FPS");
    });
    ui.checkbox(&mut app.settings.no_camera_fade, "Disable camera/character proximity fade");
    ui.add_space(10.0);
    ui.horizontal(|ui| {
        ui.label("Game runtime");
        egui::ComboBox::from_id_salt("runtime")
            .selected_text(runtime_label(app.settings.runtime))
            .show_ui(ui, |ui| {
                for choice in [
                    crate::settings::RuntimeChoice::Auto,
                    crate::settings::RuntimeChoice::Wine,
                    crate::settings::RuntimeChoice::Proton,
                ] {
                    if ui
                        .selectable_label(app.settings.runtime == choice, runtime_label(choice))
                        .clicked()
                    {
                        app.settings.runtime = choice;
                    }
                }
            });
    });
    ui.add_space(6.0);
    if !cfg!(windows) {
        let wines = ascnet_platform::wine_candidates();
        if wines.is_empty() {
            ui.colored_label(
                egui::Color32::YELLOW,
                "Wine was not found. Install your distribution's wine package or set ASCNET_WINE.",
            );
        } else {
            ui.label(
                egui::RichText::new(format!("Wine: {}", wines[0].display()))
                    .small()
                    .weak(),
            );
        }
    }
    ui.add_space(10.0);
    if ui.button("Save settings").clicked() {
        match crate::settings::save(&app.settings) {
            Ok(()) => app.push_log("Settings saved"),
            Err(error) => app.push_log(&format!("Could not save settings: {error:#}")),
        }
    }
    ui.add_space(12.0);
    ui.horizontal(|ui| {
        if ui.add_enabled(!app.busy, egui::Button::new("Check launcher update")).clicked() {
            app.start_launcher_update();
        }
    });
}

pub fn logs(app: &mut App, ui: &mut egui::Ui) {
    ui.heading("Logs");
    ui.add_space(4.0);
    egui::ScrollArea::vertical().stick_to_bottom(true).show(ui, |ui| {
        for line in &app.log {
            ui.label(line);
        }
    });
}

pub fn folder_dialog(app: &mut App, ui: &mut egui::Ui) {
    let Some(picker) = app.picker.as_mut() else {
        return;
    };
    let mut keep = true;
    let mut chosen = None;
    egui::Window::new("Select folder")
        .collapsible(false)
        .resizable(true)
        .default_width(560.0)
        .show(ui.ctx(), |ui| {
            ui.horizontal(|ui| {
                if ui.button("Up").clicked() {
                    if let Some(parent) = picker.current.parent() {
                        picker.current = parent.to_path_buf();
                        picker.reload();
                    }
                }
                ui.label(picker.current.display().to_string());
            });
            ui.separator();
            egui::ScrollArea::vertical().max_height(320.0).show(ui, |ui| {
                for entry in &picker.entries {
                    let name = entry
                        .file_name()
                        .map(|name| name.to_string_lossy().into_owned())
                        .unwrap_or_default();
                    if ui.selectable_label(false, format!("{name}/")).clicked() {
                        picker.current = entry.clone();
                        picker.reload();
                        break;
                    }
                }
            });
            ui.separator();
            ui.horizontal(|ui| {
                if ui.button("This folder").clicked() {
                    chosen = Some(picker.current.clone());
                }
                if ui.button("Cancel").clicked() {
                    keep = false;
                }
            });
        });
    if let Some(path) = chosen {
        app.settings.selected_game = Some(path);
        app.picker = None;
        app.start_client_scan();
        app.start_refresh();
    } else if !keep {
        app.picker = None;
    }
}

fn runtime_label(choice: crate::settings::RuntimeChoice) -> &'static str {
    match choice {
        crate::settings::RuntimeChoice::Auto => "Automatic (Proton if present)",
        crate::settings::RuntimeChoice::Wine => "Managed Wine prefix",
        crate::settings::RuntimeChoice::Proton => "Steam Proton prefix",
    }
}

fn patch_label(state: Option<&install::PatchState>) -> String {
    match state {
        Some(install::PatchState::Unpatched) => "retail (unpatched)".into(),
        Some(install::PatchState::Current) => "current".into(),
        Some(install::PatchState::AdoptionRequired) => "legacy install, adoption required".into(),
        Some(install::PatchState::UpdateAvailable) => "update available".into(),
        Some(install::PatchState::Unsupported(reason)) => format!("unsupported: {reason}"),
        Some(install::PatchState::RepairRequired(reason)) => format!("repair required: {reason}"),
        None => "unknown".into(),
    }
}
