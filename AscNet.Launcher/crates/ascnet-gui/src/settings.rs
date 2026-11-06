//! Launcher settings. The on-disk schema is shared with the Win32 launcher so existing
//! installations keep their selection and toggles.

use anyhow::{Context, Result};
use ascnet_platform::launcher_data_root;
use serde::{Deserialize, Serialize};
use std::{
    fs,
    path::{Path, PathBuf},
};

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Settings {
    pub selected_game: Option<PathBuf>,
    #[serde(default = "default_fps")]
    pub fps_value: i32,
    #[serde(default)]
    pub fps_enabled: bool,
    #[serde(default)]
    pub no_camera_fade: bool,
    #[serde(default)]
    pub region: Option<String>,
    /// `auto` (default), `wine`, or `proton`.
    #[serde(default)]
    pub runtime: RuntimeChoice,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum RuntimeChoice {
    #[default]
    Auto,
    Wine,
    Proton,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            selected_game: None,
            fps_value: default_fps(),
            fps_enabled: false,
            no_camera_fade: false,
            region: None,
            runtime: RuntimeChoice::Auto,
        }
    }
}

const fn default_fps() -> i32 {
    240
}

pub fn settings_path() -> Result<PathBuf> {
    if cfg!(windows) {
        let base = std::env::var_os("LOCALAPPDATA").context("LOCALAPPDATA is unavailable")?;
        return Ok(PathBuf::from(base).join("AscNet/Launcher/settings.json"));
    }
    Ok(launcher_data_root()?.join("settings.json"))
}

pub fn load() -> Settings {
    settings_path()
        .ok()
        .and_then(|path| fs::read(path).ok())
        .and_then(|bytes| serde_json::from_slice(&bytes).ok())
        .unwrap_or_default()
}

pub fn save(settings: &Settings) -> Result<()> {
    let path = settings_path()?;
    let directory = path.parent().context("settings path has no parent")?;
    fs::create_dir_all(directory)?;
    let temporary = directory.join("settings.tmp");
    fs::write(&temporary, serde_json::to_vec_pretty(settings)?)?;
    ascnet_platform::atomic_replace(&temporary, &path)
}

/// `launcher.json` beside the executable names the distributor repository.
#[derive(Clone, Debug, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DistributorConfig {
    pub repository_url: String,
    pub branch: String,
}

pub fn distributor_config() -> Option<DistributorConfig> {
    let path = launcher_file("launcher.json")?;
    serde_json::from_slice(&fs::read(path).ok()?).ok()
}

/// A file shipped beside the launcher. Inside an AppImage `current_exe` is on the read-only mount,
/// which is still the right place to read bundled metadata.
pub fn launcher_file(name: &str) -> Option<PathBuf> {
    let beside = std::env::current_exe()
        .ok()?
        .parent()?
        .join(name);
    if beside.is_file() {
        return Some(beside);
    }
    // Development fallback: the workspace copy.
    let dev = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../..")
        .join(name);
    dev.is_file().then_some(dev)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn legacy_settings_without_runtime_still_load() {
        let settings: Settings = serde_json::from_str(
            r#"{"selectedGame":"/games/pgr","fpsValue":144,"fpsEnabled":true,"musicMuted":false,"noCameraFade":true,"region":"en"}"#,
        )
        .unwrap();
        assert_eq!(settings.runtime, RuntimeChoice::Auto);
        assert_eq!(settings.fps_value, 144);
    }

    #[test]
    fn runtime_serialises_as_lowercase_kind() {
        assert_eq!(serde_json::to_string(&RuntimeChoice::Proton).unwrap(), "\"proton\"");
        assert_eq!(serde_json::to_string(&RuntimeChoice::Wine).unwrap(), "\"wine\"");
    }
}
