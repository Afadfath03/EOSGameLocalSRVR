#!/usr/bin/env bash
# AscNet local setup for Linux (user-space, no sudo).
#
# Produces the same build-state.pending.json as setup-local.ps1, so the launcher validates the
# result identically on both platforms. Installs the .NET 8 SDK and a portable mongod under the
# owned root, builds the server from the distributor checkout, and assembles the patch directory
# from a release bundle (preferred) or a local directory via ASCNET_PATCH_DIR.
set -euo pipefail

ROOT=""
REPOSITORY="https://github.com/reiserFSs/InfiniteLoop.git"
BRANCH="master"
SETUP_LOCK_HELD=0

die() { printf 'AscNet local setup: %s\n' "$*" >&2; exit 1; }
log() { printf '%s\n' "$*"; }

while [ $# -gt 0 ]; do
  case "$1" in
    --root) ROOT="${2:-}"; shift 2 ;;
    --repository) REPOSITORY="${2:-}"; shift 2 ;;
    --branch) BRANCH="${2:-}"; shift 2 ;;
    --setup-lock-held) SETUP_LOCK_HELD=1; shift ;;
    *) die "unknown argument: $1" ;;
  esac
done

[ -n "$ROOT" ] || die "--root is required"
case "$ROOT" in /*) ;; *) die "--root must be an absolute path" ;; esac
case "$BRANCH" in
  ''|[-./]*|*..*|*'@{'*|*'//'*) die "unsafe git branch name: $BRANCH" ;;
esac
case "$REPOSITORY" in
  https://github.com/*/*.git) ;;
  https://github.com/*/*) ;;
  *) die "repository must be an https://github.com/<owner>/<repo>.git URL" ;;
esac
case "$REPOSITORY" in
  *'?'*|*'#'*|*'@'*) die "repository must not contain credentials, query, or fragment" ;;
esac

mkdir -p "$ROOT"
ROOT="$(cd "$ROOT" && pwd)"

lock_fd=""
if [ "$SETUP_LOCK_HELD" -eq 0 ]; then
  exec {lock_fd}>"$ROOT/setup.lock"
  command -v flock >/dev/null || die "flock is required to serialise setup runs"
  flock -n "$lock_fd" || die "another AscNet setup is already running"
fi

for tool in git curl tar sha256sum; do
  command -v "$tool" >/dev/null || die "$tool is required"
done

tools="$ROOT/tools"
checkout="$ROOT/checkout"
build_root="$ROOT/build"
mkdir -p "$tools" "$build_root" "$ROOT/data/mongo" "$ROOT/logs"

# ── .NET 8 SDK ───────────────────────────────────────────────────────────────
dotnet=""
for candidate in "$tools/dotnet/dotnet" "$(command -v dotnet 2>/dev/null || true)"; do
  [ -n "$candidate" ] || continue
  if [ -x "$candidate" ] && "$candidate" --list-sdks 2>/dev/null | grep -q '^8\.'; then
    dotnet="$candidate"; break
  fi
done
if [ -z "$dotnet" ]; then
  log "Installing the .NET 8 SDK under $tools/dotnet"
  mkdir -p "$tools/dotnet"
  curl -fsSL --retry 3 https://dot.net/v1/dotnet-install.sh -o "$tools/dotnet-install.sh"
  bash "$tools/dotnet-install.sh" --channel 8.0 --install-dir "$tools/dotnet" --no-path >/dev/null
  dotnet="$tools/dotnet/dotnet"
  [ -x "$dotnet" ] || die ".NET 8 SDK installation did not produce dotnet"
fi

# ── mongod ───────────────────────────────────────────────────────────────────
pinned_mongod=""
if [ -f "$ROOT/build-state.json" ]; then
  pinned_mongod="$(python3 - "$ROOT/build-state.json" <<'PY'
import json,sys
try:
    print(json.load(open(sys.argv[1])).get("mongod",""))
except Exception:
    print("")
PY
)"
fi
mongod=""
if [ -n "$pinned_mongod" ] && [ -x "$pinned_mongod" ]; then
  mongod="$pinned_mongod"
elif [ -n "$pinned_mongod" ] && [ -d "$ROOT/data/mongo" ] && [ -n "$(ls -A "$ROOT/data/mongo" 2>/dev/null)" ]; then
  die "pinned MongoDB ($pinned_mongod) is missing but persistent database files exist; restore that version"
fi

if [ -z "$mongod" ]; then
  existing="$(find "$tools" -maxdepth 3 -type f -name mongod -perm -u+x 2>/dev/null | head -n1 || true)"
  if [ -n "$existing" ]; then
    mongod="$existing"
  else
    log "Resolving the current MongoDB 8.0 Community Linux x86_64 archive"
    python3 - "$tools" <<'PY' || exit 1
import hashlib, io, json, os, sys, tarfile, urllib.request, shutil
tools = sys.argv[1]
blocked = ("debian12","debian13","ubuntu2404","ubuntu2204","rhel9","amazon2023")
req = urllib.request.Request("https://downloads.mongodb.org/full.json", headers={"User-Agent": "ascnet-setup"})
data = json.load(urllib.request.urlopen(req, timeout=60))
versions = [v for v in data.get("versions", []) if v.get("production") and str(v.get("version","")).startswith("8.0.")]
if not versions:
    sys.exit("MongoDB metadata contains no stable 8.0 release")
version = max(versions, key=lambda v: [int(x) for x in v["version"].split(".")])
target = None
for entry in version.get("downloads", []):
    if entry.get("arch") != "x86_64" or entry.get("edition") != "targeted":
        continue
    if entry.get("target", "").lower() != "linux":
        continue
    if any(b in entry.get("archive", {}).get("url", "") for b in blocked):
        continue
    target = entry
    break
if target is None:
    sys.exit("MongoDB metadata has no generic Linux x86_64 archive")
url = target["archive"]["url"]
baseline = next(e["archive"]["sha256"] for e in version["downloads"] if e["archive"]["url"] == url)
if not all(c in "0123456789abcdef" for c in baseline) or len(baseline) != 64:
    sys.exit("MongoDB release has no valid SHA-256")
tmp = os.path.join(tools, "mongodb-download.tar.gz")
with urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent":"ascnet-setup"}), timeout=600) as src, open(tmp, "wb") as out:
    shutil.copyfileobj(src, out)
actual = hashlib.sha256(open(tmp, "rb").read()).hexdigest()
if actual != baseline:
    os.unlink(tmp)
    sys.exit(f"MongoDB archive checksum mismatch (expected {baseline}, received {actual})")
dest = os.path.join(tools, "mongodb-" + version["version"])
if os.path.isdir(dest):
    shutil.rmtree(dest)
os.makedirs(dest, exist_ok=True)
with tarfile.open(tmp) as tar:
    try:
        tar.extractall(dest, filter="data")
    except TypeError:
        tar.extractall(dest)
os.unlink(tmp)
print(dest)
PY
    mongod="$(find "$tools" -maxdepth 4 -type f -name mongod -perm -u+x | head -n1)"
    [ -n "$mongod" ] || die "MongoDB extraction did not produce a mongod executable"
  fi
fi

# ── checkout ─────────────────────────────────────────────────────────────────
if [ ! -d "$checkout/.git" ]; then
  log "Cloning $REPOSITORY ($BRANCH)"
  git clone --branch "$BRANCH" --single-branch "$REPOSITORY" "$checkout" >/dev/null
fi
git -C "$checkout" remote set-url origin "$REPOSITORY"
git -C "$checkout" fetch --quiet origin "$BRANCH"
git -C "$checkout" checkout --quiet --force "origin/$BRANCH"
revision="$(git -C "$checkout" rev-parse HEAD)"
[ "${#revision}" -eq 40 ] || die "checkout revision is invalid"

# ── ports and persistent config ──────────────────────────────────────────────
free_port() {
  python3 - "$@" <<'PY'
import socket, sys
excluded = {int(x) for x in sys.argv[1:]}
for _ in range(200):
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
    s.close()
    if port not in excluded:
        print(port); break
else:
    sys.exit("could not reserve a local port")
PY
}

state="$ROOT/build-state.json"
config="$ROOT/config.json"
if [ -f "$state" ]; then
  read -r sdk_port game_port mongo_port < <(python3 - "$state" <<'PY'
import json,sys
s=json.load(open(sys.argv[1]))
print(s["sdkPort"], s["gamePort"], s["mongoPort"])
PY
)
elif [ -f "$config" ]; then
  read -r game_port mongo_port < <(python3 - "$config" <<'PY'
import json,sys
c=json.load(open(sys.argv[1]))
print(c["GameServer"]["Port"], c["Database"]["Port"])
PY
)
  sdk_port="$(free_port "$game_port" "$mongo_port")"
else
  sdk_port="$(free_port)"
  game_port="$(free_port "$sdk_port")"
  mongo_port="$(free_port "$sdk_port" "$game_port")"
fi
[ "$sdk_port" != "$game_port" ] && [ "$sdk_port" != "$mongo_port" ] && [ "$game_port" != "$mongo_port" ] \
  || die "persisted ports must be distinct"

if [ ! -f "$config" ]; then
  python3 - "$checkout/Resources/Configs/config.json" "$config" "$game_port" "$mongo_port" <<'PY'
import json,sys
src,dst,game,mongo = sys.argv[1],sys.argv[2],int(sys.argv[3]),int(sys.argv[4])
c=json.load(open(src))
c.setdefault("GameServer",{}).update({"Host":"127.0.0.1","Port":game})
c.setdefault("Database",{}).update({"Host":"127.0.0.1","Port":mongo,"Name":"asc_net"})
json.dump(c, open(dst,"w"), indent=2)
PY
fi

# ── server + patch stage ─────────────────────────────────────────────────────
final="$build_root/$revision"
stage="$build_root/$revision.tmp-$$"
if [ ! -f "$final/server/AscNet.dll" ] || [ ! -f "$final/patch/supported-client.json" ]; then
  rm -rf "$stage"
  mkdir -p "$stage/server" "$stage/patch"
  log "Publishing the AscNet server"
  "$dotnet" publish "$checkout/AscNet/AscNet.csproj" -c Release -o "$stage/server" \
    --artifacts-path "$stage/dotnet-artifacts" --source https://api.nuget.org/v3/index.json >/dev/null
  [ -f "$stage/server/Configs/version_config.json" ] || die "published server is missing Configs/version_config.json"
  cp "$config" "$stage/server/Configs/config.json"

  copy_patch_dir() {
    local source="$1"
    for name in version.dll lucia.dll KRSDK.dll libraries.txt PGRBase.dll supported-client.json; do
      if [ -f "$source/$name" ]; then cp "$source/$name" "$stage/patch/$name"; fi
    done
  }

  if [ -n "${ASCNET_PATCH_DIR:-}" ]; then
    log "Using patch artifacts from $ASCNET_PATCH_DIR"
    copy_patch_dir "$ASCNET_PATCH_DIR"
  else
    # The release bundle carries the Windows DLLs built by CI; the launcher never runs them.
    manifest="$(dirname "$0")/patch-manifest.json"
    if [ -f "$manifest" ]; then
      log "Downloading the release patch bundle"
      python3 - "$manifest" "$ROOT" <<'PY'
import hashlib,json,os,sys,urllib.request,zipfile
manifest,root = sys.argv[1],sys.argv[2]
m=json.load(open(manifest))
url,digest=m["url"],m["sha256"]
if not url:
    sys.exit("patch-manifest.json has no published patch bundle (branch build); set ASCNET_PATCH_DIR")
if len(digest)!=64 or any(c not in "0123456789abcdef" for c in digest):
    sys.exit("patch-manifest.json has no valid SHA-256")
archive=os.path.join(root,"patch-bundle.zip")
with urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent":"ascnet-setup"}), timeout=300) as r, open(archive,"wb") as f:
    f.write(r.read())
if hashlib.sha256(open(archive,"rb").read()).hexdigest()!=digest:
    os.unlink(archive); sys.exit("patch bundle checksum mismatch")
with zipfile.ZipFile(archive) as z:
    for member in z.namelist():
        if "/" in member or "\\" in member or not member.endswith((".dll",".txt",".json")):
            sys.exit(f"unexpected patch bundle member: {member}")
        z.extract(member, os.path.join(root,"patch-bundle"))
os.unlink(archive)
PY
      copy_patch_dir "$ROOT/patch-bundle"
    fi
  fi

  [ -f "$stage/patch/lucia.dll" ] || die "lucia.dll is missing: set ASCNET_PATCH_DIR or provide patch-manifest.json"
  [ -f "$stage/patch/KRSDK.dll" ] || die "KRSDK.dll is missing: set ASCNET_PATCH_DIR or provide patch-manifest.json"
  [ -f "$stage/patch/version.dll" ] || die "version.dll is missing: set ASCNET_PATCH_DIR or provide patch-manifest.json"
  printf '*PGR.exe\nlucia.dll\n' > "$stage/patch/libraries.txt"
  cp "$(dirname "$0")/supported-client.json" "$stage/patch/supported-client.json"
  rm -rf "$final"
  mv "$stage" "$final"
fi

server_directory="$final/server"
cat >"$ROOT/build-state.pending.json" <<JSON
{
  "schemaVersion": 1,
  "revision": "$revision",
  "repository": "$REPOSITORY",
  "dotnet": "$dotnet",
  "mongod": "$mongod",
  "serverDirectory": "$server_directory",
  "resourceDirectory": "$server_directory",
  "patchDirectory": "$final/patch",
  "sdkPort": $sdk_port,
  "gamePort": $game_port,
  "mongoPort": $mongo_port
}
JSON

log "AscNet local build prepared for launcher validation: $revision"
log "Pending state: $ROOT/build-state.pending.json"
