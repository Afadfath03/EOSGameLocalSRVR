# Client download, verify, and update

Status: implemented. The engine is `crates/ascnet-core/src/download.rs`; the UI is the *Get game* view in
`crates/ascnet-gui/src/views.rs`. This document covers the player flow, the engine rules, and the pinned source data.

## Player guide

With no game selected, press **SELECT GAME** on Home (or **Settings → Game client…** later). The *Get game* view has:

- **Language:** English (Global), 繁體中文 (Taiwan), 한국어 (Korea), 日本語 (Japan), 简体中文 (China mainland).
  Each is a separate Kuro client at the pinned version (`supported-client.json` → `applicationVersion`); the game
  downloads its remaining resources on first start. The choice is stored in `settings.json` as `region`. With nothing
  saved it follows the OS UI language (ko → Korean, ja → Japanese, zh-CN/SG → China, other zh → Taiwan, otherwise
  English).
- **Install folder:** pick one, then the view shows what it found and what the job will cost (download size, files,
  space needed/available):
  - *Empty folder* → **DOWNLOAD** (full install; an interrupted download in the same folder resumes).
  - *Folder with `PGR.exe`* → detected edition, version, Steam copy, and AscNet patch state, plus **VERIFY & REPAIR**,
    **UPDATE** (older official version → pinned version through Kuro's patches), and **ADOPT** (bring an official
    launcher or Steam install under the launcher by verifying it in place). A detected edition locks the language
    picker so a Taiwan client is never "repaired" into English.
  - Any other non-empty folder is refused.

Install itself needs an empty folder (or one holding its own resumable `.ascnet-download/job.json`); never point
Install at a Steam install — use **ADOPT** for an existing copy.

**PAUSE / CANCEL** while a job runs; **RESUME** continues from files already verified. The launcher cannot be closed
while a job runs; pause first. When a job finishes, the folder becomes the selected game and the normal flow continues:
**SETUP** (supported-client check, local build, patch install), then **PLAY**.

Verify/repair and update never touch files outside the chosen folder and refuse to run while `PGR.exe` is running.

## Engine rules (`crates/ascnet-core/src/download.rs`)

**Pinned sources.** `supported-client.json` `"downloads"` holds, per region: the 4.8.0 CDN list in priority order,
`baseUrl`, `indexFile` + `indexFileMd5`, `size`, every `patchConfig` entry (version, baseUrl, indexFile, MD5, size),
`resourcesExcludePath`, and the live `discovery` index URLs. File lists are authenticated against the pinned MD5; the
live index is fetched once only to add CDN *hosts* after every pinned host failed. The TW index token (from the TW
`pclauncher.exe` `KRApp.conf`) is `G279/50016_i2n5NLmdCAmOGP3J1tJOlWKNSMQuyWL7`. `applicationVersion` must equal each
region's pinned version.

**List coverage.** 46.8k of the 47.2k files are under `PGR_Data/StreamingAssets/resource` (71.5 GB of 72 GB; only
347 files / 607 MB are the executable client). Install downloads all of it. Repair/Adopt — and the verify pass after
an Update — skip that prefix because the game updates it itself and drifts from the 4.8.0 index; an Update still
applies patch entries there.

**Patches.** All five regions are covered (14/14/15/13/20 versions, every list MD5-checked). Only whole-file formats
exist: keys are always `resource` (`dest, md5, size, fromFolder` | `chunkInfos` | krzip), `deleteFiles`, and `zipInfos`.
No HDiff/KrDiff entry exists in these lists. Unknown fields are rejected, so a future diff format fails with
"unsupported file list format". `fromFolder+dest` means a whole file (the 4.8.0 `zip/` tree); `.krzip` entries come
from the patch `baseUrl` and are plain deflate zips whose members match `zipInfos` exactly (names, sizes, MD5 of the
decompressed data; checked live against EN/JP 4.7.0).

**Job routing.** `plan().job` is the effective job and `note` explains any reroute:

| Detected state | Result |
| --- | --- |
| older version + patch available | Update |
| older without a patch, equal, or unreadable | verify/repair |
| newer than pinned | refused |
| different region (`KR_ProjectId` from `KRSDK.bin`, or `KRSDKConfig.json` for CN) | refused |

Install needs an empty folder or one that already holds its own `.ascnet-download/job.json` (resume).

**Variants kept, not repaired** (reported in `Outcome.skipped_variants` as `path (reason)`):

- files whose SHA-256 is in the `originals` allowlist ("accepted retail variant", e.g. Steam's `KRSDK.dll`);
- files AscNet patched, per `install::managed_files` ("AscNet patch", e.g. `KRSDK.dll`/`PGRBase.dll`; for CN only
  `PGRBase.dll`, since `version.dll`/`lucia.dll`/`libraries.txt` are not in the index and are treated like any extra);
- Steam's `KRSDK.bin` ("Steam build").

The same rule applies inside patch archives and `deleteFiles`. Extra files (steam_api, user files) are never touched.
A pending launcher transaction journal blocks Repair/Update.

**Integrity.** Every file is MD5-verified (per 100 MiB chunk for chunked files, resuming at chunk granularity; plain
files resume by length and are discarded on a final mismatch), written to `.ascnet-download/parts/`, then renamed.
krzip members are extracted with per-entry MD5 and only for names listed in `zipInfos`. Every path is validated (no
absolute paths, `..`, `\`, `:`, links, or reparse points). Deletes happen after downloads, never for files the same
patch writes, only for regular files, and empty parents are pruned. Requests use 16 MiB ranges, 8 workers, CDN failover
in priority order (hosts failing three times in a row are tried last), 3 rounds with backoff, and 4 attempts per
segment. Cancel returns `Err` whose root cause is `download::Cancelled`.

MD5 is implemented in-tree (no offline `md5` crate); `flate2` decodes the gzip-encoded `index.json`.

**Tests.** A local fixture HTTP server covers resume after cancel, corrupt chunk/file, failover (dead/503/dropped
hosts), host discovery, tampered list, traversal, Update with whole files + krzip + deletes + launcher-patched file,
Steam-like adopt, and region refusal. `cargo test --offline --lib -- --ignored live` re-checks the real CDN (all
lists/patch lists, small files, ranges on every host).

## Official source reference (global PC, no auth)

**Shared layout.** File list: `{cdn}/{config.indexFile}` → `{resource:[{dest, md5, size, chunkInfos?}]}` (47,190
files, 71.7 GB). Files over 100 MiB carry per-100 MiB chunk MD5s. File URL: `{cdn}/{config.baseUrl}{dest}`.
`config.indexFileMd5` authenticates the list; `resourcesExcludePath` is `PGR_Data/StreamingAssets/resource`.
`patchConfig` offers KrDiff patches from 14 older versions (4.7.0 → 4.8.0 ≈ 11 GB); KrDiff is HDiff + zstd (MIT
applier `hdiffpatch-rs`, as used by Vedaru/kuro). Older full builds (3.8.0, 4.1.0–4.7.0) were still downloadable per
file through versioned paths recorded in TwintailTeam history; retention is undocumented.

**EN (global).** Index:
`https://prod-alicdn-gamestarter.kurogame.com/launcher/game/G143/50015_LWdk9D2Ep9mpJmqBZZkcPBU2YNraEWBQ/index.json`
(G143 = PGR global, 50015 = global appId; token from public TwintailTeam/game-manifests). Live version 4.8.0.

**TW.** `G279/50016`, same layout. 4.8.0 base `launcher/game/G279/50016/4.8.0/RfnyCruypnrxBymaqVVowmvKlxcjZpzc/`
(47,116 files, 71.5 GB). Package `com.kurogame.punishing.grayraven.tw`; `PGR.exe` and `GameAssembly.dll` differ from EN.

**KR.** `G286/50011` (index `…/launcher/game/G286/50011_XefwDdpgPxxLABoTOD0yuqTFBC3koJZ0/index.json`). 4.8.0 base
`launcher/game/G286/50011/4.8.0/kHdNSrXondzSdDKXBidpKnhywQlKnflO/` (47,121 files, 72.1 GB; file list
`resource/50011/4.8.0/indexFile.json`, MD5 `052e1bbd…` = `indexFileMd5`). Package
`com.kurogame.punishing.grayraven.kr` (resources.assets XBuildConfig; CDN key `jqlCmYRizwT76uvX`; XUrlPerfixConfig
prod: `krcdn-ak`/`krcdn-aliyun`). `KRSDK.bin`: `KR_ProjectId=G286`, `KR_ProductId=A1794`, `KR_ChannelID=240`,
`KR_PackageName=com.herogame.pc.punishing.grayraven.kr`. `PGR.exe` and `GameAssembly.dll` differ from EN/TW;
`UnityPlayer.dll`, `PGRBase.dll`, and `PGR_Data/Plugins/KRSDK.dll` are byte-identical to EN. Statically checked: lucia
resolves through `il2cpp_*` exports (no fixed RVAs or signatures) and all 26 are exported by the KR `GameAssembly.dll`.
Native hook execution on KR is unverified (the game was not run).

**JP.** `G282/50007` (index `…/launcher/game/G282/50007_NxWGZ0d254oWqZKuuL6szOK7WRLPt668/index.json`). 4.8.0 base
`launcher/game/G282/50007/4.8.0/IuQPbGqaRgXLgaWBKpYwFBuOJqgeiYql/` (file list `resource/50007/4.8.0/indexFile.json`).
Package `com.kurogame.punishing.grayraven.jp` (XBuildConfig; CDN key `xZx901LhZhT6G2HG`; prod URLs
`jpcdn-ak`/`jpcdn-aliyun`). `KRSDK.bin`: `KR_ProjectId=G282`, `KR_ProductId=A1778`, `KR_ChannelID=240`,
`KR_PackageName=com.herogame.pc.punishing.grayraven.jp`. `PGR.exe` and `GameAssembly.dll` differ from EN/TW/KR;
`UnityPlayer.dll`, `PGRBase.dll`, and `KRSDK.dll` are byte-identical to EN. The same static `il2cpp_*` export check
passed (26/26). Native hook execution on JP is unverified (the game was not run).

**CDN bases** (from index `cdnList`): `zspms-volcdn-gamestarter.kurogame.net`,
`zspms-txcdn-gamestarter.kurogame.net`, `zspms-akcdn-gamestarter.pgr-game.com`,
`zspms-alicdn-gamestarter.kurogame.net`.

**CN (mainland, 战双帕弥什).** `G148/10012`. Discovery (gzip JSON):
`https://prod-cn-alicdn-gamestarter.kurogame.com/launcher/game/G148/10012_RnIUKs3r59Csliu3N0rl5uRWWBOFDaJL/index.json`
with backup host `https://prod-volcdn-gamestarter.kurogame.xyz/…` (same path; both returned an identical `config`,
checked live). CDNs in priority order: `zspms-alicdn-gamestarter.kurogame.com`, `zspms-txcdn-…`, `zspms-volcdn-…`.
4.8.0 base `launcher/game/G148/10012/4.8.0/NIqyHuAmTLmulqsTBArguDiUNkahynGD/` (47,052 files, 71.3 GB; file list
`resource/10012/4.8.0/indexFile.json`, MD5 `1b2e8447…`, fetched live and matching). 20 patch versions
(3.2.0 … 4.7.0).

CN has **no `KRSDK.bin` and no `KRSDK.dll`**. It ships the official `KRSDKEx.dll` + `libkrsdkcurl.dll` and a plain-JSON
`PGR_Data/Plugins/KRSDKRes/KRSDKConfig.json`. Detection falls back to that file (`KR_ProjectId=G148`, `KR_GameVersion`;
BOM/CRLF tolerated, `KR_PackageName=com.kurogame.haru.hero`). The SDK files are ordinary index files hash-validated
against the `originals` allowlist; the launcher patch only adds `version.dll`/`lucia.dll`/`libraries.txt` and patches
`PGRBase.dll`.

## Verified against the supported 4.8.0 client

- MD5 match: `PGR.exe`, stock `GameAssembly.dll`, `UnityPlayer.dll`, stock `PGRBase.dll`.
- Official `KRSDK.dll` SHA-256 `59a1d02d…` equals the second accepted entry in `supported-client.json` (Steam ships
  `2a1d8f5d…`).

## First start

The game downloads `StreamingAssets/resource` itself from Kuro's `prod-encdn-*` CDN; AscNet's `config.tab` points
there, and `lucia.dll` only redirects `client/config`, notices, and notice HTML.

## Risks

- Kuro's CDN layout is undocumented; tokens and paths can change.
- Pinned versions (client 4.8.0, document/resources 4.8.10) disappear when Kuro stops hosting them.
- About 72 GB is pulled from Kuro servers; Kuro's terms of service were not reviewed.
