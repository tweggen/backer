# Testing

Six test projects, all `net9.0` + xUnit 2.9.2 + FluentAssertions 6.12.0.
NSubstitute 5.3.0 is the mocking library (chosen because the vendored
`external/OAuth2` fork already uses it). The layered design (stub rclone,
hosted agent, full loop) is documented in `docs/plan-e2e-test-harness.md`.

| Project | Tests | Needs |
|---|---|---|
| `tests/Hannibal.Tests` | 42 | nothing — pure unit |
| `tests/WorkerRClone.Tests` | 63 + 1 opt-in | nothing — pure unit (the opt-in one needs a live OneDrive account) |
| `tests/Tools.Tests` | 9 | nothing — pure unit |
| `tests/Hannibal.IntegrationTests` | 29 | a local PostgreSQL (skips cleanly without one) |
| `tests/BackerAgent.IntegrationTests` | 15 | nothing — hosts the agent against an in-process rclone stub |
| `tests/Backer.E2ETests` | 4 | a local PostgreSQL (skips cleanly without one) |

```bash
# everything
dotnet test Backer.sln

# one project
dotnet test tests/Hannibal.Tests/

# one test
dotnet test tests/Hannibal.Tests/ --filter "FullyQualifiedName~TestMethodName"

# unit tests only — no database needed
dotnet test tests/Hannibal.Tests/ tests/WorkerRClone.Tests/ tests/Tools.Tests/
```

**No test touches live backup data, starts rclone, or writes to a cloud
account.** The single exception is the opt-in OneDrive check described at the
bottom, which is read-only and skipped by default.

## Integration tests and PostgreSQL

`tests/Hannibal.IntegrationTests` creates a **throwaway database per run**
(`backer_test_{timestamp}_{hex}`), applies all migrations, and drops it
`WITH (FORCE)` on teardown. The existing `hannibal` database is never opened.

The admin connection defaults to
`Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=admin`.
Override it with `BACKER_TEST_DB_CONNECTION` — it must point at a database
the fixture may connect to in order to `CREATE DATABASE` (conventionally
`postgres`), not at the test database itself.

```bash
# use a different server
BACKER_TEST_DB_CONNECTION="Host=db.example;Port=5432;Database=postgres;Username=x;Password=y" dotnet test tests/Hannibal.IntegrationTests/
```

If PostgreSQL is unreachable, the DB-backed tests report as **skipped** and
the run still exits 0 — a developer without PostgreSQL gets a green
`dotnet test`. The skip reason names the host/port/database it tried, never
the password. A failure *after* the server answers (e.g. a broken migration)
still fails loudly rather than hiding as a skip.

Note that a skipped run is a weaker signal than a green one. Before relying on
integration coverage, confirm the tests actually ran — a fully skipped
collection also exits 0.

### What the API harness does

`BackerApiFactory` (a `WebApplicationFactory<Program>`) hosts the real `Api`
in memory and:

- points the app at the fixture database (the `HannibalContext` descriptors
  are removed and re-added, because `AddHannibalService` resolves the
  connection string at *registration* time);
- sets `Hannibal:SkipStartupMigration=true` — the fixture already migrated;
- **removes the `RuleScheduler` hosted service**, which otherwise creates
  `Job` rows on a background loop. This is load-bearing, not a determinism
  nicety;
- overrides `Jwt:*` with deterministic test values;
- overrides `OAuth2:Providers:*` with stub credentials, so the developer's
  real client secrets — which `Api/Program.cs` loads from user-secrets — never
  reach any code path;
- replaces `IHubContext<HannibalHub>` with a recording fake so SignalR
  broadcasts are assertable without a live hub connection.

## Configuration this work introduced

| Key | Purpose |
|---|---|
| `ConnectionStrings:DefaultConnection` | DB connection string. Takes precedence over the `HANNIBAL_DB_CONNECTION` environment variable, which still works and is what the live deployment uses. Falls back to `Host=localhost;Port=5432;Database=hannibal;Username=postgres;Password=admin`. |
| `Hannibal:SkipStartupMigration` | When true, skips the `StartupMigrator` at startup (tests do this because their fixture already migrated). Default false (existing behaviour). |
| `OAuth2:RedirectUri` | OAuth2 callback URI. Defaults to `http://localhost:53682/` — the BackerAgent's local callback listener — when unset, which is the production behaviour. |

## Database migrations

The strategy and its history are in `docs/plan-db-migration-strategy.md`.
The rules it established:

- **Every schema change ships as a migration in the same PR:**
  `dotnet ef migrations add <Name> --project application/Hannibal/`.
  Enforced by `MigrationsCoverModelTests.EveryModelChangeHasAMigration`
  (`tests/Hannibal.Tests/`), which needs no database and fails the suite
  whenever the model has drifted from the migration snapshot.
- **`EnsureCreated` never returns, in any code path.** It creates schema
  without migration history and thereby breaks every future `Migrate()`.
  The server startup path is `StartupMigrator`
  (`application/Hannibal/Data/StartupMigrator.cs`): it retries transient
  connection failures, logs pending/applied migrations, and refuses a
  history-less database with a pointer to the baseline runbook.
- **Migrations are forward-only in production.** The deploy runbook is:
  back up first (`pg_dump -U postgres -Fc hannibal > hannibal-<date>.dump`
  in the `hannibal-db` container), deploy, then confirm the api log shows
  either "Database schema is up to date" or exactly the expected migrations
  being applied. Recovery is restore-from-dump, which is why the backup is
  part of the runbook, not optional.

## Opt-in live OneDrive credential check

`tests/WorkerRClone.Tests` contains one test that talks to the real Microsoft
Graph. It is **skipped unless explicitly enabled**, is read-only
(`GET /v1.0/me/drive`), never starts rclone, and never writes to the remote.

```bash
BACKER_LIVE_OAUTH_TEST=1 \
BACKER_LIVE_STORAGE_ID=<storage row id> \
BACKER_LIVE_DB_CONNECTION="Host=...;Database=hannibal;..." \
dotnet test tests/WorkerRClone.Tests/ --filter "FullyQualifiedName~LiveOneDrive"
```

| Variable | Meaning |
|---|---|
| `BACKER_LIVE_OAUTH_TEST` | Set to `1` to enable. Without it the test skips. |
| `BACKER_LIVE_STORAGE_ID` | Id of the `Storage` row to check. |
| `BACKER_LIVE_DB_CONNECTION` | Where to read that row from. Falls back to `HANNIBAL_DB_CONNECTION`. |
| `BACKER_LIVE_ONEDRIVE_CLIENT_ID` / `_SECRET` | Optional. Falls back to the values stored on the row. |
| `BACKER_LIVE_PERSIST_TOKENS` | Set to `0` to suppress writing refreshed tokens back. |

**Read this before setting `BACKER_LIVE_PERSIST_TOKENS=0`.** Microsoft rotates
the refresh token on every refresh. The test persists the rotated tokens back
to the storage row precisely because dropping them would break the *next
production run*. Suppressing the write is only safe if you know the refresh
did not rotate anything.

This check is the fastest way to answer "are the stored OneDrive credentials
actually still valid?" — the question that took a week to answer during the
July 2026 outage, when an expired Azure client secret failed silently.

## What is not covered

- No CI. `.github/workflows/` is empty. Note that `BackerControl` is
  `net9.0-windows`, so a Linux runner cannot build `Backer.sln` as a whole and
  would need a filtered project list.
- `Directory.Build.props` is untracked, so a fresh clone builds without the
  git-metadata assembly attributes it injects.
- The Blazor frontend (`Poe`) and the Avalonia desktop app (`YourBacker`) have
  no tests.
- `external/OAuth2.Tests` (the submodule's own NUnit suite) is not part of
  `Backer.sln` and does not run with `dotnet test Backer.sln`.
