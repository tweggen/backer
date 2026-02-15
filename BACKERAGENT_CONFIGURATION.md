# BackerAgent Configuration Flow

## Configuration Model

**`RCloneServiceOptions`** (`worker/WorkerRClone/Configuration/RCloneServiceOptions.cs`) is the central configuration class:

| Property | Type | Purpose |
|----------|------|---------|
| `BackerUsername` | `string?` | Credentials for API authentication |
| `BackerPassword` | `string?` | Credentials for API authentication |
| `RClonePath` | `string?` | Path to the rclone executable |
| `RCloneOptions` | `string?` | CLI options passed to rclone |
| `UrlSignalR` | `string?` | API/SignalR server URL |
| `Autostart` | `bool` | Whether to begin rclone operations automatically on startup |
| `OAuth2` | `OAuthOptions?` | Nested provider client IDs/secrets (OneDrive, Dropbox) |

## Configuration Source Layering

Sources are listed lowest to highest precedence:

1. **`appsettings.json`** — base settings (rclone path, options, API URL, empty OAuth2 provider stubs)
2. **`appsettings.{Environment}.json`** — environment-specific overrides
3. **`appsettings.{Environment}.{MachineName}.json`** — machine-specific overrides
4. **`config.json`** — runtime-writable config, loaded by `ConfigHelper`
5. **User secrets** — development only (`AddUserSecrets<Program>`)
6. **In-memory collection** — hardcoded OAuth2 client IDs and obscured secrets (`Program.cs`)
7. **Environment variables**
8. **Command-line arguments**

## ConfigHelper — Runtime Configuration Persistence

`ConfigHelper<T>` (`Tools/ConfigHelper.cs`) manages the `config.json` file:

- **Config directory resolution**:
  - Dev/Interactive mode: uses `Directory.GetCurrentDirectory()`
  - Service/Production mode: platform-specific paths via `EnvironmentDetector.GetConfigDir("Backer")`:
    - Windows: `%ProgramData%\Backer\Config`
    - macOS: `/Library/Application Support/Backer/Config`
    - Linux: `/etc/Backer` or `/var/lib/Backer/config`
- **`Save(TOptions)`**: writes the options wrapped in the section name (e.g. `{"RCloneService": {...}}`) atomically to `config.json`
- **`Load()`**: reads `config.json` and merges it into the configuration pipeline
- The `PUT /config` endpoint in `Program.cs` calls `configHelper.Save()` — this is how YourBacker (the desktop app) writes configuration to the agent

## DI Registration Chain (Program.cs)

```
ConfigHelper<RCloneServiceOptions>     <- loads config.json + user secrets + in-memory OAuth2 secrets
   | (merged into builder.Configuration)
   v
Configure<RCloneServiceOptions>        <- binds "RCloneService" section to options
AddIdentityApiClient                   <- HTTP client for token endpoint
AddBackgroundHannibalServiceClient     <- HTTP client with AutoAuthHandler (auto JWT refresh)
AddRCloneService                       <- OAuth2ClientFactory + storage providers + RCloneService hosted service
```

### AddRCloneService (worker/WorkerRClone/DependencyInjection.cs)

- Binds `RCloneServiceOptions` from the `"RCloneService"` configuration section
- Registers `OAuth2ClientFactory` as singleton, subscribing to `IOptionsMonitor<RCloneServiceOptions>` changes
- Registers storage providers via `AddStorageProviders()`
- Registers `RCloneService` as a hosted service

### AddBackgroundHannibalServiceClient (BackerAgent/DependencyInjection.cs)

- Binds `HannibalServiceClientOptions` from the `"HannibalServiceClient"` section
- Creates an `HttpClient` with `AutoAuthHandler` that:
  1. Attaches JWT token to requests
  2. Intercepts 401 Unauthorized responses
  3. Auto-refreshes token using `BackerUsername`/`BackerPassword` from `RCloneServiceOptions`
  4. Retries the original request with the new token

## RCloneConfigManager — rclone INI Configuration

Separate from the .NET configuration system, `RCloneConfigManager` (`worker/WorkerRClone/Services/RCloneConfigManager.cs`) manages the **rclone config file** (INI format with `[remoteName]` sections):

- Parses/writes rclone INI format: `LoadFromFile()`, `SaveToFile()`, `LoadFromString()`, `ExportToString()`
- Tracks remotes as `Dictionary<remoteName, Dictionary<key, value>>`
- `AddOrUpdateRemote()` returns `bool` indicating whether anything actually changed — powers the "smart restart" decision during storage reauthentication
- Thread-safe via lock
- Atomic file writes (write to `.tmp`, then copy+delete)

## Network Ports

| Port | Purpose |
|------|---------|
| **5931** | BackerAgent HTTP API (config endpoints, job control, status, SignalR hub for YourBacker desktop client) |
| **53682** | OAuth2 redirect endpoint (rclone standard callback port) |

## Runtime Configuration Endpoints

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/config` | GET | Returns current `RCloneServiceOptions` |
| `/config` | PUT | Saves new config (URL + credentials) via ConfigHelper. Strips `RClonePath`, `RCloneOptions`, `OAuth2` to prevent overwriting infrastructure settings |
| `/storages` | PUT | Updates storage-specific options at runtime |
| `/status` | GET | Returns current state machine state |
| `/start` | POST | Start job processing |
| `/stop` | POST | Stop job processing |
| `/quit` | POST | Shut down the service |
| `/jobs/{jobId}/abort` | POST | Abort a specific running job |
| `/jobtransfers` | GET | Get current job transfer statistics |
| `/health` | GET | Health check |

The `PUT /config` endpoint deliberately nulls out `RClonePath`, `RCloneOptions`, and `OAuth2` before saving — these are infrastructure settings that should only come from `appsettings.json` or in-memory defaults, not be overwritten by the desktop control app.

## Key Files

| File | Purpose |
|------|---------|
| `BackerAgent/Program.cs` | Application startup, DI wiring, endpoint definitions |
| `BackerAgent/appsettings.json` | Base configuration |
| `BackerAgent/config.json` | Runtime-writable configuration (credentials, API URL) |
| `worker/WorkerRClone/Configuration/RCloneServiceOptions.cs` | Central options model |
| `worker/WorkerRClone/DependencyInjection.cs` | `AddRCloneService` registration |
| `BackerAgent/DependencyInjection.cs` | `AddBackgroundHannibalServiceClient` registration |
| `Tools/ConfigHelper.cs` | Runtime config load/save to `config.json` |
| `Tools/AutoAuthHandler.cs` | JWT auto-refresh HTTP handler |
| `Tools/ConstantTokenProvider.cs` | In-memory JWT token holder |
| `Tools/NetworkIdentifierHostedService.cs` | Network change monitoring |
| `worker/WorkerRClone/Services/RCloneConfigManager.cs` | rclone INI config manager |
| `application/Hannibal/OAuth2ClientFactory.cs` | OAuth2 client creation per provider |
