#!/usr/bin/env bash
# Builds AscNetLauncher-x86_64.AppImage from target/release/ascnet-launcher.
# Requires appimagetool on PATH (CI downloads the continuous AppImage release).
set -euo pipefail

here="$(cd "$(dirname "$0")/.." && pwd)"
binary="$here/target/release/ascnet-launcher"
[ -x "$binary" ] || { echo "build the release binary first: cargo build --release -p ascnet-cli" >&2; exit 1; }
command -v appimagetool >/dev/null || { echo "appimagetool is required" >&2; exit 1; }

appdir="$here/target/AppDir"
rm -rf "$appdir"
mkdir -p "$appdir/usr/bin" "$appdir/usr/share/applications" "$appdir/usr/share/icons/hicolor/256x256/apps"

cp "$binary" "$appdir/usr/bin/ascnet-launcher"
for file in supported-client.json setup-local.sh; do
  cp "$here/$file" "$appdir/usr/bin/$file"
done
if [ -f "$here/patch-manifest.json" ]; then
  cp "$here/patch-manifest.json" "$appdir/usr/bin/patch-manifest.json"
fi
chmod +x "$appdir/usr/bin/ascnet-launcher" "$appdir/usr/bin/setup-local.sh"

cp "$here/packaging/linux/AppRun" "$appdir/AppRun"
chmod +x "$appdir/AppRun"
cp "$here/packaging/linux/ascnet-launcher.desktop" "$appdir/ascnet-launcher.desktop"
cp "$here/packaging/linux/ascnet-launcher.desktop" "$appdir/usr/share/applications/ascnet-launcher.desktop"
cp "$here/packaging/linux/ascnet-launcher.png" "$appdir/ascnet-launcher.png"
cp "$here/packaging/linux/ascnet-launcher.png" "$appdir/usr/share/icons/hicolor/256x256/apps/ascnet-launcher.png"

output="${OUTPUT:-$here/AscNetLauncher-x86_64.AppImage}"
APPIMAGE_EXTRACT_AND_RUN=1 ARCH=x86_64 appimagetool "$appdir" "$output"
echo "$output"
