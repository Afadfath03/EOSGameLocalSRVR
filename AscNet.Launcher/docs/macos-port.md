# macOS port (parked design note)

Status: design agreed 2026-09-27; **not implemented**. The Linux port now follows the same shape — cross-platform
window, managed Wine/Proton runtime, native server — so see `platforms.md` for the parts that already exist.

## Decisions

- **UI:** one cross-platform Rust window shared with Windows. No SwiftUI.
- **Runtime:** require the user's own licensed CrossOver first. A free runtime fallback is out of scope for now.
- **Signing:** no Apple Developer ID will be obtained. The build ships unsigned and unnotarized (ad-hoc signature
  only). Users must allow it once past Gatekeeper (Open from the context menu, or
  `xattr -d com.apple.quarantine`). [INFERENCE] Files written by the launcher's own updater are not quarantined, so
  self-updates should not trigger Gatekeeper again; verify before release.

## Flow

1. Detect CrossOver at `/Applications/CrossOver.app/Contents/SharedSupport/CrossOver`; refuse with guidance if absent.
2. Create an AscNet bottle through CrossOver's documented CLI (`cxbottle --create --template win10_64`) and install
   corefonts, d3dcompiler_47, and vcrun2015–2022. Registry: `RetinaMode=N`, 1920x1080 window settings.
3. Download the pinned client from Kuro into the bottle (see `client-download.md`). No Steam inside the bottle.
4. Apply the Rosetta client patches in Rust, with hash-recorded backups:
   - `PGRBase.dll`: `0F 1F C2` → `90 90 90` at the unique signature
     `21 ca 21 ca 81 f2 b3 a5 d6 7a [0f 1f c2] 41 8b 0a 50 48 8d 05` (stable across builds so far).
   - `GameAssembly.dll`: register-form NOP inside `.tvm0`, located by a masked pattern. The pattern changes with most
     game updates (4.8.0 matched the fifth variant of `pgr_apply_patches.py`), so each supported client version pins
     its pattern in `supported-client.json`; the launcher never guesses.
   - Unity startup stub: already implemented in `crates/ascnet-core/src/pgrbase.rs`.
   - `supported-client.json` already accepts the Rosetta-patched `GameAssembly.dll` hash.
5. Server: the macOS equivalent of `setup-local.ps1` installs the .NET 8 SDK and MongoDB from official archives (not
   Homebrew), clones and builds AscNet, and obtains the Windows patch DLLs the same way Linux does (release bundle or
   a local patch directory).
6. Install the AscNet patch (`lucia.dll`, `version.dll`, `KRSDK.dll`) through `crates/ascnet-core/src/install.rs`.
7. Launch `wineloader PGR.exe` from the game directory with the environment from `launch-pgr-ascnet.sh`
   (D3DMetal/DXMT paths, `d3d11,dxgi=n,b`, esync/fsync/msync off) **plus** `version=n,b` and `ASCNET_PATCH_ORIGIN`,
   which that script omits. Run MongoDB and the server natively.

## Code reuse

- **Shared as is:** `install.rs`, `package.rs`, `pgrbase.rs`, `supported-client.json`, updater verification.
- **New for macOS:** Rosetta patcher, CrossOver bottle manager, Wine launcher, client downloader, macOS setup script.
  The cross-platform window already exists, which removes the "replace the Win32 UI" item from this plan.
- **Updater:** releases gain a separate macOS archive.

## Order

1. Shared core: Rosetta patcher with restorable backups, Wine launcher, client downloader.
2. Cross-platform window on Windows first (parity). *(done)*
3. macOS: CrossOver detection, bottle setup, macOS setup script, play.
4. macOS release archive and updater support.

## Risks

- A new `GameAssembly.dll` Rosetta pattern is needed per game update.
- Rosetta is being phased out; [INFERENCE] CrossOver is the only runtime with an announced ARM64 path.
- DXMT requires macOS 14+ on Apple Silicon.
- Unsigned app: Gatekeeper friction on first launch.
- Client/resource version pinning and Kuro CDN changes (see `client-download.md`).
