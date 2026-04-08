# Specification: .NET 10 Local LLM Gateway

### Core Idea
This application acts as a secure, local, transparent bridge for CLI tools and IDEs. It solves the developer experience (DevEx) problem of authenticating with secured downstream LLM services, specifically Bifrost Enterprise, without requiring those tools to natively support complex Entra ID interactive flows. It abstracts the authentication layer, maintains a persistent local session, and forwards raw payloads to configured endpoints in under 2 milliseconds.

---

### 1. Application Foundation & Configuration Domain
The application is an ASP.NET Core web host running as a local console executable via Kestrel, listening on a predefined local port (e.g., `http://localhost:8080`).

* **Runtime Engine:** .NET 10 SDK.
* **Core Packages:** `Yarp.ReverseProxy`, `Microsoft.Identity.Client`, `Microsoft.Identity.Client.Extensions.Msal`, `Microsoft.Extensions.Hosting`.
* **Configuration Location:** The application dynamically bypasses standard `appsettings.json` conventions. Configuration strictly resides at `~/.config/<app name>/settings.json`.
    * Path resolution uses `Environment.SpecialFolder.UserProfile` to guarantee identical behavior across Windows, macOS, and Linux (e.g., resolving to `/home/user/.config/...` or `C:\Users\user\.config\...`).
* **Cross-Platform Token Caching:** To prevent repeated interactive logins, the MSAL token cache is serialized to the exact same `~/.config/` directory. OS-native secure storage is utilized automatically: DPAPI for Windows, Keychain for macOS, and LibSecret for Linux.

### 2. Identity & Authentication Domain (Entra ID)
This domain manages the interactive user authorization and establishes the session upon application startup.

* **Interactive Logon:** Before Kestrel accepts traffic, MSAL.NET initiates an Interactive Public Client flow, opening the system browser for the developer to authenticate against the enterprise Entra ID tenant.
* **Scope Definition:** The application requests an access token explicitly scoped for the Bifrost Enterprise app registration (e.g., `api://<bifrost-app-id>/.default`), rather than the default Microsoft Graph scopes, ensuring valid audience (`aud`) claims.

### 3. Background Maintenance & Token State Aspect
A background process actively manages the 60 to 90-minute lifespan of the Entra ID token to prevent sudden 401 Unauthorized errors mid-session.

* **State Container:** A lightweight, thread-safe singleton class (`TokenProvider`) holds the current access token. To maximize performance during concurrent YARP reads, it utilizes `Volatile.Read` and `Volatile.Write` on a private string field, avoiding expensive thread locks.
* **Refresh Mechanism:** An `IHostedService` runs an infinite loop with a `PeriodicTimer` set to exactly 30 seconds.
* **Nuance:** The service calls MSAL's `AcquireTokenSilent()`. MSAL intelligently returns the cached token immediately and only executes a network request to Entra ID if the current token is within 5 minutes of expiration. The service then atomically updates the `TokenProvider` with the valid string.

### 4. Routing & Proxying Domain (YARP)
This domain routes local traffic, mutates outbound headers, and forwards the payload identically to the upstream Bifrost gateway.

* **Route Mapping:** Incoming paths are mapped via YARP's `{**catch-all}` configuration to specific Bifrost endpoints, mimicking standard OpenAI or Anthropic payload schemas locally.
* **Header Injection Transform:** A custom YARP Transform executes on every outbound request:
    1.  Resolves the `TokenProvider` singleton.
    2.  Injects the retrieved Entra ID token as `Authorization: Bearer <token>`.

### 5. Bifrost Authentication Flow & Virtual Keys Aspect
Bifrost Enterprise natively handles OpenID Connect (OIDC) integration, downloading the tenant's configuration and verifying the JWT signature mathematically.

* **Implicit Mapping:** If a request arrives with only the Bearer token, Bifrost extracts the Object ID (OID) and automatically resolves it to the developer's default budget and load-balanced LLM pool.
* **Explicit Mapping (Optional Virtual Keys):** Developers can target specific budgets (e.g., a premium project) by routing to specific local paths. YARP configuration supports a `Metadata` dictionary per route.
* **Transform Nuance:** The custom YARP transform evaluates `context.Route.Metadata.TryGetValue("VirtualKey", ...)`. If a virtual key is defined for the matched route, the transform dynamically injects the `x-bf-vk: <virtual_key>` header alongside the Bearer token. Bifrost then cross-references the Entra ID OID against the Access Control List (ACL) of that specific virtual key to authorize the transaction.

---

### Correlations & System Relationships
The local application acts strictly as an identity broker. The human interactive login translates into a machine-readable JWT, which is kept fresh in the background and blindly stapled to standard API payloads. Because Bifrost Enterprise enforces all governance, budgeting, and rate-limiting at its own infrastructure layer, the local gateway contains zero complex business logic or validation rules. The integration relies entirely on the unidirectional flow of the token from MSAL, to the volatile state container, to the YARP transform pipeline, and finally to the Bifrost validation engine.