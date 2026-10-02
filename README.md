# EntraAuthProxy

A small .NET reverse proxy that signs you in with Microsoft Entra ID and adds your access token to requests sent to an upstream API. It lets CLI tools and IDEs use Entra-protected APIs without implementing interactive Entra authentication themselves.

Your client talks to the proxy using the upstream API's existing request format. The proxy handles sign-in, token caching, background renewal, forwarding, and optional static request headers. The upstream gateway remains responsible for authorization, model access, budgets, and rate limits.

## TL;DR: setup and run

You need the **.NET 10 SDK**, an upstream API URL, and an Entra public-client app registration permitted to request that API's scope. Obtain the tenant ID, client ID, and target scope from whoever manages your Entra/API integration. The app registration must support browser-based public-client sign-in with the loopback redirect URI used below (`http://localhost:5000`). A browser and working OS credential storage are needed for interactive sign-in and persistent caching.

From the repository root:

```sh
cp sample-entraauthproxy.json entraauthproxy.json
```

Edit `entraauthproxy.json` with your actual values:

```json
{
  "EntraAuth": {
    "TenantId": "<tenant-id>",
    "ClientId": "<public-client-app-id>",
    "TargetScope": "api://<upstream-api-app-id>/.default"
  },
  "TargetAddress": "https://<your-gateway>/",
  "Headers": {},
  "Port": 5000,
  "RedirectPort": 5000,
  "DangerousAcceptAnyServerCertificate": false,
  "ForceInteractiveAuthentication": false
}
```

Start the proxy:

```sh
dotnet run --project EntraAuthProxy --no-launch-profile -- --config "$PWD/entraauthproxy.json"
```

`dotnet run` restores dependencies and builds automatically. `--no-launch-profile` bypasses the repository's developer-specific launch settings, including `Debug` overrides for both logging levels. The commands above use a POSIX shell; in PowerShell, use `Copy-Item` to copy the sample and pass the configuration's absolute path to `--config`.

Complete browser sign-in if prompted, then point your client at `http://localhost:5000`, including whatever API path the upstream expects. For example, if your gateway exposes an OpenAI-compatible `/v1` API, use `http://localhost:5000/v1` as the client's base URL:

```sh
curl http://localhost:5000/v1/models
```

The example endpoint must exist on your upstream. The proxy supplies the Entra bearer token, so clients do not need to obtain one. If a client insists on an API-key setting that it sends as `Authorization`, a placeholder is sufficient: the proxy replaces that header. Other authentication headers are not automatically removed. Stop the proxy with `Ctrl+C`.

**Listener behavior:** setting `Port` currently binds Kestrel to `http://*:<port>` on all interfaces, even when your client uses `localhost`. There is no inbound authentication in this application; anyone able to reach the listener can send requests using your upstream identity. Restrict access to the listener using your host/network configuration.

## How it works

```text
CLI / IDE ──HTTP──> Kestrel + YARP ──Bearer token──> Upstream API
                         ▲
                   TokenProvider
                         ▲
             MSAL sign-in and silent refresh
                         ↕
             Entra ID / persistent token cache
```

1. **Load configuration.** The application reads its JSON settings, validates and captures logging overrides and optional custom request headers, sets the listening port, and configures console logging.
2. **Authenticate before accepting traffic.** MSAL builds a public client for the configured tenant and client ID. It first tries silent authentication using the first cached account. If user interaction is required, it opens a browser. Forced interactive sign-in skips the silent attempt. Kestrel starts only after authentication succeeds.
3. **Persist the session.** MSAL Extensions registers a persistent token cache using OS-protected storage: Windows protection, macOS Keychain, or Linux Keyring. Cache persistence is verified during startup. The cache name contains a SHA-256 hash of the tenant ID, client ID, and target scope, separating those authentication configurations.
4. **Keep a token ready.** The singleton `TokenProvider` stores the access token with `Volatile.Read` and `Volatile.Write`, allowing concurrent requests to read the current token without an explicit lock.
5. **Refresh in the background.** `TokenRefreshService` calls MSAL's `AcquireTokenSilent` every 30 seconds and updates the token when successful. MSAL decides whether to reuse its cached token or obtain a new one; the proxy does not implement its own expiry threshold. Individual proxied requests do not initiate authentication.
6. **Forward requests.** YARP's default catch-all route forwards requests to `TargetAddress`. A request transform replaces `Authorization` with `Bearer <access-token>` and applies any configured static request headers on every route.

The application does not parse or translate LLM payloads or implement model-specific endpoints. YARP handles HTTP forwarding, and API compatibility depends on the upstream service. Use a scope for the **upstream API**, rather than an unrelated resource such as Microsoft Graph, so the token targets the intended service.

## Stack and source layout

| Component | Technology / responsibility |
| --- | --- |
| Runtime and HTTP host | C# on .NET 10, ASP.NET Core, Kestrel |
| Reverse proxy | YARP (`Yarp.ReverseProxy` 2.1.0) |
| Entra authentication | MSAL (`Microsoft.Identity.Client` 4.61.3) |
| Persistent token cache | `Microsoft.Identity.Client.Extensions.Msal` 4.61.3 |
| CLI parsing | `System.CommandLine` 2.0.0-beta4.22272.1 |
| Background work | .NET hosting `BackgroundService` and `PeriodicTimer` |

Package versions are centrally declared in `Directory.Packages.props`.

| File | Purpose |
| --- | --- |
| `EntraAuthProxy/Program.cs` | CLI validation and application host setup |
| `EntraAuthProxy/ConfigurationLocations.cs` | Shared base, profile, explicit-file, and local lookup paths |
| `EntraAuthProxy/StartupConfiguration.cs` | JSON loading, overlays, starter generation, logging/header capture before authentication, and the cache-directory boundary |
| `EntraAuthProxy/StartupLogging.cs` | Selected environment-variable validation and captured startup logging thresholds |
| `EntraAuthProxy/CustomRequestHeaders.cs` | Shared loading and reserved-header validation for an immutable startup header map |
| `EntraAuthProxy/ProxyExtensions.cs` | Generated YARP route, custom configuration overlays, and bearer-token/static-header transform |
| `EntraAuthProxy/AuthExtensions.cs` | MSAL client creation, secure cache registration, and startup sign-in |
| `EntraAuthProxy/TokenProvider.cs` | Shared in-memory access token |
| `EntraAuthProxy/TokenRefreshService.cs` | Periodic silent token acquisition |
| `sample-entraauthproxy.json` | Starter configuration |

## Configuration

The application clears the standard ASP.NET Core configuration sources and loads its own JSON files. Do not rely on `appsettings.json` or ordinary environment-variable overrides for application settings.

Configuration is selected as follows:

1. **Explicit file:** `--config /absolute/path/settings.json` (or `-c`) loads only that file, taking precedence over automatic environment and default lookup. A missing explicit file is an error. Combining either config option with `--profile` is a CLI error, in either argument order, before any directory creation, JSON loading, sign-in, or listener startup.
2. **Base directory:** without an explicit file, `ENTRAAUTHPROXY_CONFIG_DIR` selects the complete application base directory when its value is not empty or whitespace. Otherwise, the base is `~/.config/entraauthproxy`. An environment-selected base does not receive another `entraauthproxy` directory segment.
3. **Selected global JSON:** `--profile <name>` selects `<base>/<name>/entraauthproxy.json`. Without a profile, the selected file is `<base>/entraauthproxy.json`. A profile uses the existing JSON schema; the unprofiled base JSON is not loaded as an additional source or fallback.
4. **Working-directory overlay:** when the base-directory environment variable is unset, empty, or whitespace, the application then loads `entraauthproxy.json` from the current working directory if present. Local values override matching properties; properties omitted locally retain their selected global values. A local file can load alone when the selected global file is missing. An effective environment override disables local lookup, with or without a profile.

If neither the selected global JSON nor an eligible local file exists, the application creates the selected directory and writes its existing starter JSON there, reports its path, and exits successfully before sign-in. With a profile, the starter goes inside the profile folder even if the base directory already contains an unprofiled JSON file. Edit the starter and restart. On Windows, `~` denotes the user profile too, so the default base is `<UserProfile>\.config\entraauthproxy`.

Profile names require a value and must be non-empty identifiers containing only Unicode letters (category `L`), Unicode decimal digits (`Nd`), hyphens, or underscores. For example, `work-prod_2` and `παραγωγή_٢` are valid. Dots, spaces, directory separators, rooted paths, other punctuation, and combining marks are rejected as CLI errors; names are not silently repaired.

Select a profile under the default base:

```sh
dotnet run --project EntraAuthProxy --no-launch-profile -- --profile work
```

This selects `~/.config/entraauthproxy/work/entraauthproxy.json`, followed by any eligible working-directory overlay. To select `/custom/config/work/entraauthproxy.json` and disable that overlay:

```sh
ENTRAAUTHPROXY_CONFIG_DIR=/custom/config dotnet run --project EntraAuthProxy --no-launch-profile -- --profile work
```

The token cache always uses the **base directory**: the effective `ENTRAAUTHPROXY_CONFIG_DIR` value, or `~/.config/entraauthproxy` otherwise. A profile does not move the cache into its folder, and an explicit configuration file does not move it next to that file. Cache names and OS credential-store identifiers are unchanged; profiles using the same tenant, client, and scope share the same cache.

| Setting | Meaning |
| --- | --- |
| `EntraAuth.TenantId` | Entra tenant identifier; replace the starter and sample's `Entra tenant ID` placeholder. |
| `EntraAuth.ClientId` | Public-client application ID used for sign-in. |
| `EntraAuth.TargetScope` | Single scope requested for the upstream API, such as `api://<app-id>/.default`. |
| `TargetAddress` | Upstream base URL for the generated catch-all route. |
| `Headers` | Optional flat object of header names and single static string values, applied to every outgoing proxied request. Omitted or `{}` adds no headers; `Authorization` is reserved in every capitalization. |
| `Port` | Proxy HTTP listening port; the sample uses `5000`. Binds all interfaces. |
| `RedirectPort` | Local browser sign-in callback port. Falls back to `Port` when omitted. |
| `DangerousAcceptAnyServerCertificate` | Disables upstream TLS certificate validation when `true`; leave `false` for normal use. |
| `ForceInteractiveAuthentication` | Forces browser authentication at startup when `true`. |
| `ReverseProxy` | Optional YARP configuration layered over the generated routes and clusters. |

`ENTRAAUTHPROXY_FORCE_INTERACTIVE` overrides `ForceInteractiveAuthentication` when present: only the value `true` (case-insensitive) enables it; any other value disables it.

`Port` and `RedirectPort` can be equal because startup authentication happens before Kestrel begins listening. Ensure the callback port is available during sign-in. Although JSON files are loaded with change watching enabled, several values are captured during startup; restart after configuration changes to apply them consistently.

### Startup logging

Set either of these environment variables when starting the proxy:

| Environment variable | Minimum logging level | Fallback when unset |
| --- | --- | --- |
| `ENTRAAUTHPROXY_LOG_LEVEL` | Default for categories without a more specific rule | `Information` |
| `ENTRAAUTHPROXY_ASPNETCORE_LOG_LEVEL` | `Microsoft.AspNetCore` and its subcategories | `Warning` |

The settings work independently with default discovery, `--profile`, `ENTRAAUTHPROXY_CONFIG_DIR`, and `-c`/`--config`. Accepted values are `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, and `None`, case-insensitively. Surrounding whitespace is trimmed; missing, empty, and whitespace-only values retain the fallback for that variable. Other nonblank values, including numbers and comma-separated combinations, fail startup after JSON loading with an error on standard error naming the variable and allowed values, before sign-in, persistent token-cache access, or listener startup.

When automatic discovery creates a starter configuration, the process exits successfully before validating these overrides. Validation occurs after you fill in the starter and restart.

For example, using the configuration created in the setup steps:

```sh
ENTRAAUTHPROXY_LOG_LEVEL=Error \
ENTRAAUTHPROXY_ASPNETCORE_LOG_LEVEL=Debug \
dotnet run --project EntraAuthProxy --no-launch-profile -- --config "$PWD/entraauthproxy.json"
```

This allows ASP.NET Core debug logs while the default threshold remains `Error`. Setting only the default to `Debug` retains `Warning` for ASP.NET Core. Native logging rules choose the most specific matching provider/category rule, so existing more specific category or provider rules retain their precedence. `None` suppresses logs governed by that setting; it does not change the other threshold or suppress direct console messages such as authentication and starter-configuration output.

These two variables are read explicitly; ordinary environment-variable configuration for application settings remains disabled. Their values are captured once per startup, and watched JSON reloads retain the captured thresholds. Restart the proxy to apply changes. Console logging retains its existing single-line output and UTC timestamp format.

### Custom request headers

Add a top-level `Headers` object to send static request headers to your upstream. Merge this fragment into your existing configuration:

```json
{
  "Headers": {
    "X-Client-Name": "my-client",
    "X-Environment": "development"
  }
}
```

Each value is a single string applied as configured, with no interpolation or automatic prefix. Empty string values are allowed and do not act as header-removal directives. Normal HTTP handling applies to request and content headers. Omitting `Headers`, or setting it to `{}`, preserves existing forwarding behavior.

The map applies to every forwarded request on the generated catch-all and all `ReverseProxy` routes, for every HTTP method and whether a bearer token is available. Header names match case-insensitively. Each configured value replaces all existing outgoing values for that name, including multiple caller values and values set by YARP route transforms. Static headers are applied after route transforms.

Existing configuration selection and overlay rules apply to this map. An eligible local overlay overrides same-name global/profile entries case-insensitively and retains inherited entries it omits. An empty local `Headers` object does not erase inherited headers. An effective `ENTRAAUTHPROXY_CONFIG_DIR` continues to disable local lookup.

`Authorization` is reserved because the proxy manages the Entra bearer token. Any capitalization of that name in the effective `Headers` map, even with an empty value, stops startup before sign-in, persistent token-cache access, or listener startup.

The effective map is validated and captured once before startup authentication. Restart to apply additions, edits, or removals. Watched JSON reloads and YARP route rebuilds continue to use that startup snapshot.

### Custom routing

By default, the proxy creates a route named `catch-all` matching `{**catch-all}` and a cluster named `target-cluster` with one destination. A `ReverseProxy` section can override these entries or add routes and clusters. Added routes coexist with the generated catch-all route.

For example, add the following top-level section to send `/premium/...` through the same upstream and remove the `/premium` prefix before forwarding:

```json
"ReverseProxy": {
  "Routes": {
    "premium": {
      "ClusterId": "target-cluster",
      "Order": -1,
      "Match": { "Path": "/premium/{**catch-all}" },
      "Transforms": [ { "PathRemovePrefix": "/premium" } ]
    }
  }
}
```

With this route, `/premium/v1/models` forwards as `/v1/models`. The Entra bearer-token and configured static-header transform applies to all configured routes.

## Build and publish

```sh
dotnet build EntraAuthProxy.sln
dotnet publish EntraAuthProxy/EntraAuthProxy.csproj -c Release -r <runtime-identifier> --self-contained true -o ./publish
```

Replace `<runtime-identifier>` with your target, for example `osx-arm64`, `linux-x64`, or `win-x64`. The project enables single-file publishing and names the executable `entraauthproxy` (`entraauthproxy.exe` on Windows). Build output otherwise uses the repository's `artifacts` layout.

To run the local verification after a default Debug build (the repository check requires Python 3):

```sh
dotnet run --project tests/ProxyBehaviorChecks --no-build --no-launch-profile
dotnet run --project tests/ConfigurationBehaviorChecks --no-build --no-launch-profile
python3 tests/verify_repository.py
```

The proxy harness uses loopback proxy/upstream listeners and deterministic tokens to check routing, authorization replacement, header and payload forwarding, static-header collisions, empty/content headers, concurrency, and the startup snapshot across JSON/YARP reload and restart. The configuration harness exercises the production resolver and loader with temporary default/environment bases, profiles, overlays, explicit files, starter creation, and a stand-in authentication boundary that checks cache placement and reserved-header/logging validation. It checks actual logger filtering and captured events, native category/provider precedence, watched JSON reloads, and fixed startup logging/header snapshots. The Python check validates CLI errors and Unicode names, pre-authentication reserved-header and logging failures, configuration and logging examples, real startup starter generation, and the maintained-file inventory. These checks need no Entra credentials and do not change your real configuration or persistent token cache. Run `python3 tests/verify_cli.py` for just the CLI validation cases.

Run the published executable with the same configuration option:

```sh
./publish/entraauthproxy --config /absolute/path/entraauthproxy.json
```

## Troubleshooting

- **A starter configuration was created and the process exited:** fill in the real identity and upstream settings, then restart.
- **Startup rejects `Headers` containing `Authorization`:** remove that entry in every capitalization from the selected configuration and any eligible local overlay; the proxy manages the bearer token.
- **Custom header changes have not taken effect:** restart the proxy after editing `Headers`; JSON reloads and route rebuilds retain the startup values.
- **Startup rejects a logging level:** use one of the named levels listed above; numeric levels and combined values are rejected.
- **Logging override changes have not taken effect:** restart with the updated environment variables; JSON reloads retain the startup thresholds.
- **Browser sign-in or consent fails:** check the tenant, public-client app registration, loopback redirect URI, and permission/consent for the target API scope.
- **Token-cache persistence fails:** check access to the configuration directory and OS credential storage. Linux requires a usable keyring; the application has no plaintext-cache fallback.
- **Requests fail after a previously working session:** background refresh catches errors and retries on later ticks, retaining the previous token. It does not prompt for login or log refresh failures. If renewed interaction is required, restart with forced interactive authentication.
- **The upstream returns 401 or 403:** check that the scope targets that API and that the signed-in user has the required upstream permissions.
- **An address is already in use:** check both the proxy port and browser callback port, and update configuration as needed.
