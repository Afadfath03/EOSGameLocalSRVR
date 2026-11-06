# PGR Local Server

A personal, local-only server for **Punishing: Gray Raven (PGR)**, built on
[AscNet](https://github.com/rafi1212122/AscNet) and adapted for PC/Steam client experiments. It runs on your own
machine against a local MongoDB. It is not an official Kuro Games service.

**Current target:** PC/Steam client **4.8.0** (document version **4.8.12**), channel **205**. Protocol and gameplay
support differ per feature; nothing here is a claim of parity with the retail service.

## What works

- SDK/HTTP and game TCP server: local accounts, login, client configuration, notices, and player data in MongoDB.
- Steam/PC bridge through `run_steam.py` and `proxy.py`: starts the server, checks configuration, optionally manages a
  local MongoDB, routes traffic, and launches the client.
- Account and progression: characters, level/skills, weapons, memories, items, stages, events, rewards, login notices.
- Compatibility work for draws and pity, events, sign-in, equipment, dorm, missions, and several game modes.
- Implemented modes: Theatre, Theatre3–6, Circuit Connect, and Babylonia. Depth and known limits differ per mode.
- Native launcher for supported clients, targeting EN, TW, KR, JP, and CN. A supported configuration is not proof that
  every region has been tested against the real client.

Many features read the client tables directly. Where no server rule exists, AscNet's own policy is used and marked as
such in the relevant document (for example, leaderboards are local-only, and Fate draw rules are documented local
policy rather than recovered retail odds). See `Resources/Configs/draw-rules-source.md` and
`Resources/Configs/purchase-rules-source.md`.

## Requirements

- .NET SDK 8
- MongoDB
- Python 3.10+ (bridge)
- mitmproxy/mitmdump (bridge mode)
- A PGR PC/Steam client matching the pinned version

## Run the server

Start MongoDB first, then from the repository root:

```bash
dotnet run --project AscNet/AscNet.csproj -- --urls http://127.0.0.1:8080
```

Configuration lives in `Resources/Configs/config.json`. Defaults: game server `127.0.0.1:2335`, MongoDB
`127.0.0.1:27017`, database `asc_net`.

## Run the Steam/PC bridge

```bash
python3 run_steam.py --with-mongo
```

Start the client after the server and proxy are ready:

```bash
python3 run_steam.py --with-mongo --launch-cmd ./launch-pgr-ascnet.sh
```

See all options with `python3 run_steam.py --help`. The example `launch-pgr-ascnet.sh` contains macOS/CrossOver paths
and must be adjusted on other machines. On Windows, point the launcher at the correct KRSDK cache path when using the
cache repair/seeding options.

## Launcher

`AscNet.Launcher` is a Rust workspace with a cross-platform window and CLI. It downloads and verifies the official
client, installs the AscNet patch, and runs the local server on demand.

- **Windows:** the client runs natively; installing into a protected folder uses the consented elevation path.
- **Linux:** the launcher is a native AppImage; the Windows client runs through Wine or an adopted Steam Proton prefix.
  The server and MongoDB stay native.

Build and packaging details, prerequisites, and troubleshooting: `AscNet.Launcher/docs/platforms.md`.

## GM tool

[PGR GM Tool](../pgr-gm-tool/README.md) is a separate dashboard for accounts and player data in the same local
database. Run and configure it independently; it does not connect to any official account.

## Verify

Compatibility harness:

```bash
dotnet run --project AscNet.Test/AscNet.Test.csproj
```

The harness exposes focused switches (`--...-only`); the full list is in `AscNet.Test/Program.cs`, with examples in
this README's history. Build the server with `dotnet build AscNet.sln`.

A green harness is not a full native playthrough: retail combat, rendered UI, and movie playback are not all verified.

## Local data

Do not commit runtime data such as `.runtime/`, MongoDB files, proxy/server logs, packet captures, client binaries, or
credentials. `bin/` and `obj/` are build output and stay local as well.

## Credits

This project is local development based on [AscNet](https://github.com/rafi1212122/AscNet). Tables and client contracts
come from PGR client resources; the game and its assets remain the property of their respective rights holders.
