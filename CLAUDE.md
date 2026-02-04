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

# Run tests
dotnet test tests/WorkerRClone.Tests/

# Run a single test
dotnet test tests/WorkerRClone.Tests/ --filter "FullyQualifiedName~TestMethodName"

# Apply database migrations (from Hannibal project)
dotnet ef database update --project application/Hannibal/

# Run individual projects
dotnet run --project Api/                    # Web API
dotnet run --project BackerAgent/            # Background service
dotnet run --project frontend/Poe/           # Blazor web UI

# Docker deployment
docker-compose up
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
- Database connection string: `ConnectionStrings:DefaultConnection`
- BackerAgent credentials: `RCloneService:BackerUsername/BackerPassword`
