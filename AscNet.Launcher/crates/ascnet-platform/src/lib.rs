//! Operating-system boundary for the AscNet launcher.
//!
//! Everything the launcher does to the machine lives here: where state is stored, whether
//! installing needs elevation, how a child process tree is terminated, how the game is
//! launched, how Steam installations are discovered.
//!
//! Unix hosts launch the Windows client through Wine or Proton; the patch DLLs remain Windows
//! binaries in every case.

use anyhow::{bail, Result};
use serde::{Deserialize, Serialize};
use std::{
    collections::BTreeMap,
    path::{Path, PathBuf},
};

#[cfg(windows)]
mod windows;
#[cfg(windows)]
pub use windows::*;

#[cfg(unix)]
mod linux;
#[cfg(unix)]
pub use linux::*;

/// How a Windows client is executed on this host.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", tag = "kind")]
pub enum GameRuntime {
    /// Native Windows execution of `PGR.exe`.
    Native,
    /// A Wine prefix created and owned by the launcher.
    ManagedWine { prefix: PathBuf, wine: PathBuf },
    /// A Proton prefix owned by Steam (ADOPT flow).
    Proton {
        prefix: PathBuf,
        proton: PathBuf,
        app_id: String,
        steam_root: PathBuf,
    },
}

/// A game folder found under a Steam library, with the runtime needed to start it.
#[derive(Clone, Debug)]
pub struct SteamInstall {
    pub game: PathBuf,
    pub app_id: Option<String>,
    pub runtime: GameRuntime,
}

/// Environment applied to `PGR.exe`; the patch DLLs read these.
#[derive(Clone, Debug, Default)]
pub struct LaunchOptions {
    pub origin: String,
    pub fps: Option<i32>,
    pub no_camera_fade: bool,
}

pub fn launcher_data_root() -> Result<PathBuf> {
    let root = data_root()?.join("AscNetLauncher");
    std::fs::create_dir_all(&root)?;
    Ok(root)
}

pub fn game_running() -> Result<bool> {
    running_under()
}

pub fn install_blockers(client: &Path) -> Result<Vec<String>> {
    let mut blockers = Vec::new();
    if !client.join("PGR.exe").is_file() {
        blockers.push("PGR.exe is missing from the selected folder".to_owned());
    }
    if game_running().unwrap_or(true) {
        blockers.push("PGR.exe is running; close the game first".to_owned());
    }
    Ok(blockers)
}

// ───────────────────────── Steam library discovery (shared) ─────────────────────────

/// Steam roots plus every library folder listed in `libraryfolders.vdf`.
pub fn steam_libraries(root: &Path) -> Vec<PathBuf> {
    let mut result = vec![root.to_path_buf()];
    let file = root.join("steamapps/libraryfolders.vdf");
    let Ok(text) = std::fs::read_to_string(file) else {
        return result;
    };
    if let Ok(fields) = vdf_object_fields(&text, "libraryfolders") {
        for value in fields.values() {
            let path = PathBuf::from(value);
            if path.join("steamapps").is_dir() {
                result.push(path);
            }
        }
    }
    // Modern VDF nests each library's path in a numbered object.
    if let Ok(tokens) = vdf_tokens(&text) {
        for pair in tokens.windows(2) {
            if pair[0].eq_ignore_ascii_case("path") {
                let path = PathBuf::from(&pair[1]);
                if path.join("steamapps").is_dir() {
                    result.push(path);
                }
            }
        }
    }
    result.sort();
    result.dedup();
    result
}

pub fn vdf_object_fields(text: &str, object: &str) -> Result<BTreeMap<String, String>> {
    let tokens = vdf_tokens(text)?;
    let start = tokens
        .iter()
        .position(|value| value.eq_ignore_ascii_case(object))
        .ok_or_else(|| anyhow::anyhow!("missing VDF object {object}"))?;
    let mut fields = BTreeMap::new();
    let mut index = start + 1;
    if tokens.get(index).map(String::as_str) != Some("{") {
        bail!("invalid VDF object {object}");
    }
    index += 1;
    let mut depth = 1;
    while index < tokens.len() && depth > 0 {
        match tokens[index].as_str() {
            "{" => {
                depth += 1;
                index += 1;
            }
            "}" => {
                depth -= 1;
                index += 1;
            }
            key if depth == 1
                && tokens
                    .get(index + 1)
                    .is_some_and(|value| value != "{" && value != "}") =>
            {
                fields.insert(key.to_ascii_lowercase(), tokens[index + 1].clone());
                index += 2;
            }
            _ => index += 1,
        }
    }
    if depth != 0 {
        bail!("unterminated VDF object {object}");
    }
    Ok(fields)
}

fn vdf_tokens(text: &str) -> Result<Vec<String>> {
    let bytes = text.as_bytes();
    let mut out = Vec::new();
    let mut index = 0;
    while index < bytes.len() {
        while index < bytes.len() && bytes[index].is_ascii_whitespace() {
            index += 1;
        }
        if index >= bytes.len() {
            break;
        }
        if bytes[index] == b'/' && bytes.get(index + 1) == Some(&b'/') {
            while index < bytes.len() && bytes[index] != b'\n' {
                index += 1;
            }
            continue;
        }
        if matches!(bytes[index], b'{' | b'}') {
            out.push((bytes[index] as char).to_string());
            index += 1;
            continue;
        }
        if bytes[index] != b'"' {
            bail!("invalid VDF token at byte {index}");
        }
        index += 1;
        let mut value = String::new();
        while index < bytes.len() && bytes[index] != b'"' {
            if bytes[index] == b'\\'
                && index + 1 < bytes.len()
                && matches!(bytes[index + 1], b'\\' | b'"')
            {
                index += 1;
            }
            value.push(bytes[index] as char);
            index += 1;
        }
        if index == bytes.len() {
            bail!("unterminated VDF string");
        }
        index += 1;
        out.push(value);
    }
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_nested_modern_vdf_and_escapes() {
        let text =
            r#""libraryfolders" { "0" { "path" "C:\\Program Files\\Steam" "apps" { "1" "2" } } }"#;
        let tokens = vdf_tokens(text).unwrap();
        assert!(tokens
            .windows(2)
            .any(|pair| pair == ["path", "C:\\Program Files\\Steam"]));
    }
}
