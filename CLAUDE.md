# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Backer is a distributed backup/file synchronization system supporting multiple workstations, cloud providers (OneDrive, Dropbox, Google Drive), local NAS devices, and Nextcloud. The system uses rclone as the underlying file transfer engine.

## Build Commands

```bash
# Build the entire solution
dotnet build Backer.sln

# Build for release
dotnet build Backer.sln -c Release

# Run all tests (see docs/TESTING.md)
dotnet test Backer.sln

# Run unit tests only - no database needed
dotnet test tests/Hannibal.Tests/ tests/WorkerRClone.Tests/ tests/Tools.Tests/

# Run a single test
dotnet test tests/Hannibal.Tests/ --filter "FullyQualifiedName~TestMethodName"

# Apply database migrations (from Hannibal project)
dotnet ef database update --project application/Hannibal/

# Run individual projects
dotnet run --project Api/                    # Web API
dotnet run --project BackerAgent/            # Background service
dotnet run --project frontend/Poe/           # Blazor web UI

# Docker deployment
docker-compose up

# Windows installer (Inno Setup). Fetch the bundled git client first - the
# compile aborts with "No files found matching ...\contrib\git\*" without it.
powershell -ExecutionPolicy Bypass -File contrib\fetch-mingit.ps1
dotnet publish BackerAgent\BackerAgent.csproj -c Release -r win-x64
dotnet publish YourBacker\YourBacker.csproj  -c Release -r win-x64
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" BackerInstaller.iss
```

## Technology Stack

- **.NET 9.0** (SDK 9.0.308 - see global.json)
- **Blazor Server** for web frontend (Poe)
- **Avalonia** for cross-platform desktop (YourBacker)
- **Entity Framework Core 9.0** with PostgreSQL
- **SignalR** for real-time communication
- **xUnit + FluentAssertions** for testing

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                    FRONTEND LAYER                       │
│  Poe (Blazor Web UI)       YourBacker (Avalonia Desktop)│
└──────────────────────────┬──────────────────────────────┘
                           │ HTTP/SignalR
                           ▼
┌─────────────────────────────────────────────────────────┐
│                   API LAYER (Api/)                      │
│  - ASP.NET Core Web API                                 │
│  - JWT authentication                                   │
│  - RuleScheduler (event-driven job creation)            │
│  - Core business logic in application/Hannibal/         │
└──────────────────────────┬──────────────────────────────┘
                           │ HTTP/SignalR
                           ▼
┌─────────────────────────────────────────────────────────┐
│               WORKER LAYER (BackerAgent/)               │
│  - Windows Service / Linux Systemd                      │
│  - WorkerRClone (worker/WorkerRClone/)                  │
│  - RCloneStateMachine controls service lifecycle        │
│  - Storage providers for OAuth2 & local storage         │
└──────────────────────────┬──────────────────────────────┘
                           │
                           ▼
                    ┌──────────────┐
                    │   rclone     │ (external CLI tool)
                    └──────────────┘
```

## Key Projects

| Project | Path | Purpose |
|---------|------|---------|
| Api | `Api/` | ASP.NET Core Web API with JWT auth, Swagger docs |
| Hannibal | `application/Hannibal/` | Core library: services, EF Core models, scheduling |
| BackerAgent | `BackerAgent/` | Background service host (Windows/Linux) |
| WorkerRClone | `worker/WorkerRClone/` | RClone wrapper, state machine, storage providers |
| Poe | `frontend/Poe/` | Blazor web frontend |
| YourBacker | `YourBacker/` | Avalonia cross-platform desktop control app |
| Tools | `Tools/` | Shared utilities (auth handlers, token services) |
| Hannibal.Tests | `tests/Hannibal.Tests/` | Unit tests: scheduling, path overlap, OAuth2 client |
| WorkerRClone.Tests | `tests/WorkerRClone.Tests/` | Unit tests: password obscurer, Graph provider |
| Tools.Tests | `tests/Tools.Tests/` | Unit tests: HTTP auth handlers |
| Hannibal.IntegrationTests | `tests/Hannibal.IntegrationTests/` | API + DB integration (needs PostgreSQL) |

## Key Domain Concepts

- **Rule**: Defines what to backup (source/destination endpoints, schedule)
- **Job**: A single execution instance of a Rule
- **Storage**: Cloud or local storage configuration (credentials, provider type)
- **Endpoint**: A specific path within a Storage
- **RuleScheduler**: Event-driven scheduler that creates Jobs from Rules

## Authentication

JWT-based authentication with auto-refresh:
- API issues tokens via `/api/authb/v1/token`
- `AutoAuthHandler` in BackerAgent intercepts 401s and auto-refreshes
- Frontend uses cookie auth with `HttpContextTokenProvider` for API calls
- JWT config in `appsettings.json` under `Jwt:Key`, `Jwt:Issuer`, `Jwt:Audience`

## Storage Providers

Located in `worker/WorkerRClone/Services/Providers/`:
- **OAuth2**: OneDrive, Dropbox, Google Drive
- **Local**: SMB, Local filesystem, Nextcloud

## Configuration

- `appsettings.json` - base configuration
- `appsettings.{Environment}.json` - environment overrides
- `appsettings.{MachineName}.json` - machine-specific
- Database connection string: `ConnectionStrings:DefaultConnection`, else the
  `HANNIBAL_DB_CONNECTION` environment variable (what the live deployment uses),
  else a localhost fallback
- `Hannibal:SkipStartupMigration` - skip the StartupMigrator at startup
  (default false; tests set it because their fixture already migrated)
- `OAuth2:RedirectUri` - OAuth2 callback, defaults to `http://localhost:53682/`
  (the BackerAgent's local listener)
- BackerAgent credentials: `RCloneService:BackerUsername/BackerPassword`
- `GitWorker:GitPath` - the git executable for the git transfer engine
  (default `git`, i.e. a PATH lookup). The Windows installer bundles MinGit
  and rewrites this to an absolute path; without a working git >= 2.31 the
  agent never advertises the `git` capability and git jobs stay queued
- `RCloneService:SkipJobAcquisition` - agent logs in, connects and starts rclone
  but never acquires jobs, rclone or git (for smoke tests beside the user's
  live agents); `GitWorker:SkipJobAcquisition` gates only the git worker

## Database Migrations

Every schema change ships as an EF migration in the same PR
(`dotnet ef migrations add <Name> --project application/Hannibal/`) — a
no-database unit test fails the suite when the model drifts from the
migration snapshot. Never use `EnsureCreated`; the startup path is
`StartupMigrator` (`application/Hannibal/Data/StartupMigrator.cs`).
Deploy runbook and rationale: `docs/plan-db-migration-strategy.md` and
`docs/TESTING.md`.

## Testing

See `docs/TESTING.md`. Six test projects; `tests/Hannibal.IntegrationTests`
and `tests/Backer.E2ETests` need a local PostgreSQL and skip cleanly without
one. No test touches live backup data or starts rclone.
