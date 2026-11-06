# CN SDK and CDN support

Scope: the mainland China client (`com.kurogame.haru.kuro`, 4.8.0). This document covers its configuration entry,
proxy rewrite rules, local account login, verification commands, and the native launcher/patch path.

## Configuration (`Resources/Configs/version_config.json`)

CN is a package entry with the region's authoritative `ConfigRows`, like KR and JP. Only AscNet-owned rows are
templated:

- `PayCallbackUrl`, `ServerListStr`, and `ChannelServerListStr` use `{origin}`.
- Everything else is served verbatim, including the CN CDN list, document/launch hashes, and `Channel`.

`ChannelServerListStr` keeps the retail per-login-channel groups (`18|19|46|56|167`), each pointing at
`{origin}/api/Login/Login-cn`. `AscNet.Test/Fixtures/Region/cn.tab` is the retail table the regression compares
against.

## Proxy behaviour

CN `config.tab` (`prod-zspns-txcdn` / `prod-zspnsalicdn` `.kurogame.com`) passes through to the real CDN. The proxy's
`response()` rewrites every `;`-separated gate URL in `ServerListStr` and `ChannelServerListStr` to
`ASCNET_PROXY_TARGET/api/Login/Login-cn` without a query (the client appends `?loginType=...`). Patch files and
agreement pages stay upstream.

The proxy routes `sdkapi.kurogame.com/sdkcom/...` to AscNet and sinks `/ad-service/v1/sendEvent`. CN is identified by
the SDK package, original host, project/product headers, or form fields; locally generated player-config links carry
`region=cn`. EN/TW/KR/JP keep their existing configuration and login contracts.

## Local account login

AscNet policy: the first CN `/sdkcom/v2/login/accLogin.lg` request carrying `loginName`, `password`, and optional
`loginType` creates the account, using the same plaintext store as `/api/AscNet/register`. Existing accounts require a
matching password (fixed-time compare); a failed login never overwrites it.

## Auto login and tokens

CN automatic login accepts `autoToken` or `token` and resolves the persisted `Account.Token`. Responses use that token
for `code`, `token`, and `accessToken`. `autoToken` is `0.<Account.Token>.<UTC + 30 days>`, which auto login unwraps
without checking the timestamp; bare tokens are also accepted. Nothing is rotated or expired: the timestamp and
`expires_in` are SDK compatibility metadata. Obsolete `cn_auto_token` fields in existing account documents are
ignored.

## Gate behaviour

`/api/Login/Login-cn`, and any gate request with `region=cn`, never maps an unknown token to the Steam fallback
account. `thirdLogin.accLogin` and the `phone` UI switch are enabled; `accReg` stays off because accounts are created
by account login and there is no SMS. `age` is the AscNet compatibility constant `CnSdkCompatibilityAge`.

## Verification

```text
python3 -m unittest test_run_steam.py test_proxy.py
dotnet run --project AscNet.Test/AscNet.Test.csproj -- --cn-sdk-config-only
dotnet run --project AscNet.Test/AscNet.Test.csproj -- --cn-sdk-login-only   # needs MongoDB; cleans up its accounts
```

## Native launcher and patch (no proxy)

The CN client keeps its official SDK and its own login UI: `PGR_Data/Plugins/KRSDKEx.dll`, `libkrsdkcurl.dll`, and the
plain-JSON `KRSDKRes/KRSDKConfig.json` (G148/A1393, channel 19). There is no `KRSDK.dll` and no `KRSDK.bin`.

The launcher therefore installs only `version.dll`, `lucia.dll`, `libraries.txt`, and the PGRBase startup stub. The SDK
files are hash-checked against `supported-client.json` and never replaced. Inside the game, `lucia.dll` preloads
`libkrsdkcurl.dll`, hooks its `kr_sdk_curl_easy_setopt` (the only curl import KRSDKEx uses to set a URL), and rewrites
`CURLOPT_URL` values whose path is `/sdkcom/*` or `/ad-service/v1/sendEvent` (any `sdkapi*.kurogame.com|xyz` host) to
`ASCNET_PATCH_ORIGIN`. Agreement pages, payment, analytics, and OAuth hosts are untouched. Unity requests (CN
`config.tab`, notices) are routed by the existing path rules, so `ServerListStr` leads the client to
`/api/Login/Login-cn`.

Relevant log lines: `[lucia] OK KRSDKConfig.json identity`, `[lucia] OK CN SDK hook install`,
`[lucia] routed <url> -> <url>`. See `AscNet.Launcher/docs/player-diagnostics.md` for the full table.

## Player data and telemetry

Per-player SDK data lives in `%APPDATA%\KR_G148\A1393\` (agreement, cache); the SDK shows its privacy agreement first.
The helper process `KRSDKExternal.exe` uses its own libcurl and still reports analytics to Kuro (`KR_DATA_HOST`,
`KR_ShushuAddress`). That is retail SDK behaviour and is not routed.

## Not covered

`/sdkcom/v2/heartbeat/statusCheck.lg`, `sys/conf/cps.lg`, SMS/QR/idcard/third-party login, and `/sdk/trade/*` payment
endpoints are not implemented by the server.
