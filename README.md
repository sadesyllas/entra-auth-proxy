# EntraInjectorProxy

A small .NET reverse proxy that signs you in with Microsoft Entra ID and adds your access token to requests sent to an upstream API. It lets CLI tools and IDEs use Entra-protected LLM gateways, such as Bifrost Enterprise, without implementing interactive Entra authentication themselves.

Your client talks to the proxy using the upstream API's existing request format. The proxy handles sign-in, token caching, background renewal, and forwarding. The upstream gateway remains responsible for authorization, model access, budgets, and rate limits.

## TL;DR: setup and run

You need the **.NET 10 SDK**, an upstream API URL, and an Entra public-client app registration permitted to request that API's scope. Obtain the tenant ID, client ID, and target scope from whoever manages your Entra/API integration. The app registration must support browser-based public-client sign-in with the loopback redirect URI used below (`http://localhost:5000`). A browser and working OS credential storage are needed for interactive sign-in and persistent caching.

From the repository root:

```sh
cp sample-entrainjectorproxy.json entrainjectorproxy.json
```

Edit `entrainjectorproxy.json` with your actual values:

```json
{
  "EntraAuth": {
    "TenantId": "<tenant-id>",
    "ClientId": "<public-client-app-id>",
    "TargetScope": "api://<upstream-api-app-id>/.default"
  },
  "TargetAddress": "https://<your-gateway>/",
  "VirtualKey": "",
  "Port": 5000,
  "RedirectPort": 5000,
  "DangerousAcceptAnyServerCertificate": false,
  "ForceInteractiveAuthentication": false
}
```

Start the proxy:

```sh
dotnet run --project EntraInjectorProxy --no-launch-profile -- --config "$PWD/entrainjectorproxy.json"
```

`dotnet run` restores dependencies and builds automatically. `--no-launch-profile` bypasses the repository's developer-specific launch settings. The commands above use a POSIX shell; in PowerShell, use `Copy-Item` to copy the sample and pass the configuration's absolute path to `--config`.

Complete browser sign-in if prompted, then point your client at `http://localhost:5000`, including whatever API path the upstream expects. For example, if your gateway exposes an OpenAI-compatible `/v1` API, use `http://localhost:5000/v1` as the client's base URL:

```sh
curl http://localhost:5000/v1/models
```

The example endpoint must exist on your upstream. The proxy supplies the Entra bearer token, so clients do not need to obtain one. If a client insists on an API-key setting that it sends as `Authorization`, a placeholder is sufficient: the proxy replaces that header. Other authentication headers are not automatically removed. Stop the proxy with `Ctrl+C`.

**Listener behavior:** setting `Port` currently binds Kestrel to `http://*:<port>` on all interfaces, even when your client uses `localhost`. There is no inbound authentication in this application; anyone able to reach the listener can send requests using your upstream identity. Restrict access to the listener using your host/network configuration.

## How it works

```text
CLI / IDE ──HTTP──> Kestrel + YARP ──Bearer token + optional x-bf-vk──> Upstream API
                         ▲
                   TokenProvider
                         ▲
             MSAL sign-in and silent refresh
                         ↕
             Entra ID / persistent token cache
```

1. **Load configuration.** The application reads its JSON settings, sets the listening port, and configures console logging.
2. **Authenticate before accepting traffic.** MSAL builds a public client for the configured tenant and client ID. It first tries silent authentication using the first cached account. If user interaction is required, it opens a browser. Forced interactive sign-in skips the silent attempt. Kestrel starts only after authentication succeeds.
3. **Persist the session.** MSAL Extensions registers a persistent token cache using OS-protected storage: Windows protection, macOS Keychain, or Linux Keyring. Cache persistence is verified during startup. The cache name contains a SHA-256 hash of the tenant ID, client ID, and target scope, separating those authentication configurations.
4. **Keep a token ready.** The singleton `TokenProvider` stores the access token with `Volatile.Read` and `Volatile.Write`, allowing concurrent requests to read the current token without an explicit lock.
5. **Refresh in the background.** `TokenRefreshService` calls MSAL's `AcquireTokenSilent` every 30 seconds and updates the token when successful. MSAL decides whether to reuse its cached token or obtain a new one; the proxy does not implement its own expiry threshold. Individual proxied requests do not initiate authentication.
6. **Forward requests.** YARP's default catch-all route forwards requests to `TargetAddress`. A request transform replaces `Authorization` with `Bearer <access-token>`. If the matched route has nonempty `VirtualKey` metadata, it also adds an `x-bf-vk` header.

The application does not parse or translate LLM payloads or implement model-specific endpoints. YARP handles HTTP forwarding, and API compatibility depends on the upstream service. Use a scope for the **upstream API**, rather than an unrelated resource such as Microsoft Graph, so the token targets the intended service.

For Bifrost integrations, the optional virtual key identifies the key to send upstream. Token validation, identity-to-key mapping, and access policy enforcement depend on your Bifrost deployment; this proxy does not implement them.

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
| `EntraInjectorProxy/Program.cs` | CLI options, configuration loading, host setup, YARP routes, and header transforms |
| `EntraInjectorProxy/AuthExtensions.cs` | MSAL client creation, secure cache registration, and startup sign-in |
| `EntraInjectorProxy/TokenProvider.cs` | Shared in-memory access token |
| `EntraInjectorProxy/TokenRefreshService.cs` | Periodic silent token acquisition |
| `sample-entrainjectorproxy.json` | Starter configuration |

## Configuration

The application clears the standard ASP.NET Core configuration sources and loads its own JSON files. Do not rely on `appsettings.json` or ordinary environment-variable overrides for application settings.

Configuration is selected as follows:

1. **Explicit file:** `--config /absolute/path/settings.json` (or `-c`) loads only that file. A missing explicit file is an error.
2. **Environment-selected directory:** without `--config`, setting `ENTRAINJECTORPROXY_CONFIG_DIR` loads `entrainjectorproxy.json` from that directory and disables current-directory configuration lookup.
3. **Default locations:** otherwise, the application loads `~/.config/entrainjectorproxy/entrainjectorproxy.json`, then overlays `entrainjectorproxy.json` from the current working directory if present. Local values take precedence.

If no configuration file is found through automatic lookup, the application creates a starter file in the selected global directory and exits. Edit it and restart. On Windows, `~` denotes the user profile too, so the default directory is `<UserProfile>\.config\entrainjectorproxy`.

The token cache always uses `ENTRAINJECTORPROXY_CONFIG_DIR` when set, or `~/.config/entrainjectorproxy` otherwise. Selecting an explicit configuration file does **not** move the token cache next to that file.

| Setting | Meaning |
| --- | --- |
| `EntraAuth.TenantId` | Entra tenant identifier; the sample uses `organizations`. |
| `EntraAuth.ClientId` | Public-client application ID used for sign-in. |
| `EntraAuth.TargetScope` | Single scope requested for the upstream API, such as `api://<app-id>/.default`. |
| `TargetAddress` | Upstream base URL for the generated catch-all route. |
| `VirtualKey` | Optional `x-bf-vk` value for the generated route; blank omits injection. |
| `Port` | Proxy HTTP listening port; the sample uses `5000`. Binds all interfaces. |
| `RedirectPort` | Local browser sign-in callback port. Falls back to `Port` when omitted. |
| `DangerousAcceptAnyServerCertificate` | Disables upstream TLS certificate validation when `true`; leave `false` for normal use. |
| `ForceInteractiveAuthentication` | Forces browser authentication at startup when `true`. |
| `ReverseProxy` | Optional YARP configuration layered over the generated routes and clusters. |

`ENTRAINJECTORPROXY_FORCE_INTERACTIVE` overrides `ForceInteractiveAuthentication` when present: only the value `true` (case-insensitive) enables it; any other value disables it.

`Port` and `RedirectPort` can be equal because startup authentication happens before Kestrel begins listening. Ensure the callback port is available during sign-in. Although JSON files are loaded with change watching enabled, several values are captured during startup; restart after configuration changes to apply them consistently.

### Custom routing

By default, the proxy creates a route named `catch-all` matching `{**catch-all}` and a cluster named `target-cluster` with one destination. A `ReverseProxy` section can override these entries or add routes and clusters. Added routes coexist with the generated catch-all route.

For example, add the following top-level section to send `/premium/...` through the same upstream using a different virtual key and remove the `/premium` prefix before forwarding:

```json
"ReverseProxy": {
  "Routes": {
    "premium": {
      "ClusterId": "target-cluster",
      "Order": -1,
      "Match": { "Path": "/premium/{**catch-all}" },
      "Metadata": { "VirtualKey": "<premium-virtual-key>" },
      "Transforms": [ { "PathRemovePrefix": "/premium" } ]
    }
  }
}
```

With this route, `/premium/v1/models` forwards as `/v1/models`. The Entra bearer-token transform applies to all configured routes. Virtual-key injection is based on each matched route's metadata; additional routes do not automatically inherit the top-level `VirtualKey`.

## Build and publish

```sh
dotnet build EntraInjectorProxy.sln
dotnet publish EntraInjectorProxy/EntraInjectorProxy.csproj -c Release -r <runtime-identifier> --self-contained true -o ./publish
```

Replace `<runtime-identifier>` with your target, for example `osx-arm64`, `linux-x64`, or `win-x64`. The project enables single-file publishing and names the executable `entrainjectorproxy` (`entrainjectorproxy.exe` on Windows). Build output otherwise uses the repository's `artifacts` layout.

Run the published executable with the same configuration option:

```sh
./publish/entrainjectorproxy --config /absolute/path/entrainjectorproxy.json
```

## Troubleshooting

- **A starter configuration was created and the process exited:** fill in the real identity and upstream settings, then restart.
- **Browser sign-in or consent fails:** check the tenant, public-client app registration, loopback redirect URI, and permission/consent for the target API scope.
- **Token-cache persistence fails:** check access to the configuration directory and OS credential storage. Linux requires a usable keyring; the application has no plaintext-cache fallback.
- **Requests fail after a previously working session:** background refresh catches errors and retries on later ticks, retaining the previous token. It does not prompt for login or log refresh failures. If renewed interaction is required, restart with forced interactive authentication.
- **The upstream returns 401 or 403:** check that the scope targets that API and that the signed-in user has the required upstream permissions and virtual-key access.
- **An address is already in use:** check both the proxy port and browser callback port, and update configuration as needed.
