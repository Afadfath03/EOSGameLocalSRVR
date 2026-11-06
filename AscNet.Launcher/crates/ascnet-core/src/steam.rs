//! Steam library discovery. The platform layer knows the per-OS Steam roots and Proton prefixes.

use anyhow::Result;
use ascnet_platform::{GameRuntime, SteamInstall};
use std::path::{Path, PathBuf};

pub fn discover_game() -> Result<Option<PathBuf>> {
    Ok(steam_installs().into_iter().next().map(|install| install.game))
}

pub fn steam_installs() -> Vec<SteamInstall> {
    ascnet_platform::steam_installs()
}

/// Finds the Steam installation that owns `game`, so the launcher can reuse its Proton prefix
/// instead of creating a second one.
pub fn install_for(game: &Path) -> Option<SteamInstall> {
    let game = std::fs::canonicalize(game).unwrap_or_else(|_| game.to_path_buf());
    steam_installs()
        .into_iter()
        .find(|install| std::fs::canonicalize(&install.game).is_ok_and(|path| path == game))
}

pub fn valid_game_directory(path: &Path) -> bool {
    path.is_dir() && path.join("PGR.exe").is_file()
}

/// Runtime to use for a game folder: the Steam Proton prefix when one exists, the managed Wine
/// prefix otherwise. `None` when Steam only has a plain game copy and no prefix.
pub fn runtime_for(game: &Path) -> Option<GameRuntime> {
    install_for(game).map(|install| install.runtime)
}
