# Database migration strategy for the server

Status: Gates M1 and M3 executed 2026-08-21; M2 diagnosis run 2026-08-21 —
production is healthy, no baseline needed (see Gate M2). Open: M2's backup +
one observed clean redeploy (which also closes M3 AC2), and Gate M4.

## Context

The live deployment (Coolify, `docker-compose.yml` at the repo root) evolves
its PostgreSQL schema by "stop the stack, redeploy, and let the Api migrate at
startup". That has not been reliable. This plan explains why, and replaces
hope with a mechanism.

### What the startup path actually does today (verified 2026-08-21, `eddc9f3`)

On boot, unless `Hannibal:SkipStartupMigration` is set, `Api/Program.cs:801-811`
runs two steps in sequence:

1. `hannibalContext.Database.Migrate()` (`Api/Program.cs:807`) — synchronous,
   **no retry, no logging of what it is about to apply**. If PostgreSQL is not
   yet accepting connections, this throws and the container dies.
2. `InitializeDatabaseAsync()` (`application/Hannibal/Data/HannibalContext.cs:44`)
   — which calls **`Database.EnsureCreatedAsync()`** in a 60-second retry loop
   (`HannibalContext.cs:56`), then runs a dev-content seeder.

### The three defects in that path

**1. `EnsureCreated` and `Migrate` are mutually poisonous.**
`EnsureCreatedAsync` creates the full current-model schema **without** the
`__EFMigrationsHistory` table. Any database it created can never be migrated:
`Migrate()` sees no history, replays the initial migration, and dies on
`relation "AspNetRoles" already exists`. The migrations only begin
2026-01-01 (`application/Hannibal/Migrations/`), and the first one —
`20260101121727_AddedOAuth2Email`, despite its name — is the **full schema**
(12 `CreateTable`s). So if the production database predates 2026-01-01, or was
ever (re)created by the `EnsureCreatedAsync` path, `Migrate()` at startup
fails on every deploy. This is the most likely cause of "it did not work
well". Whether production is in this state is checked in Gate M2 with one SQL
probe.

**2. The startup race with PostgreSQL has no guard.**
`docker-compose.yml` gives `hannibal-db` a healthcheck but the `api` service
has **no `depends_on`** — on every stack redeploy the Api may start first. The
retry loop lives in step 2 (`HannibalContext.cs:51-75`), but step 1,
`Migrate()`, runs *before* it with no retry at all (`Api/Program.cs:807`). A
slow Postgres start kills the Api container even when the schema is fine.

**3. The seeder is buggy and runs in production.**
`InitializeDatabaseAsync` gates dev seeding on `if (!!await Rules.AnyAsync())`
(`HannibalContext.cs:77`) — the double negation means "seed when rules already
exist", the opposite of any plausible intent. `_createDevContent`
(`HannibalContext.cs:113-181`) then early-returns when storages exist, which
is the only reason production is not repeatedly reseeded. Hardcoded
`timo`-user content does not belong in the server startup path at all.

### What already works, and is kept

- **Migrations are complete.** `Migrate()` on an empty database produces the
  full schema with zero pending migrations afterwards — proven on every
  integration-test run by `PostgresFixture`
  (`tests/TestSupport.Api/PostgresFixture.cs:138-146` asserts exactly that).
- **The opt-out flag.** Tests and out-of-band deployments keep
  `Hannibal:SkipStartupMigration` (`Api/Program.cs:801`,
  `tests/TestSupport.Api/BackerApiFactory.cs:198`).
- **Connection resolution.** `ConnectionStrings:DefaultConnection` →
  `HANNIBAL_DB_CONNECTION` → localhost fallback
  (`application/Hannibal/DependencyInjection.cs:36-54`), covered by
  `tests/Hannibal.IntegrationTests/ConnectionStringResolutionTests.cs`.
- **EF Core 9 migration locking.** `MigrateAsync` takes a Postgres advisory
  lock, so even if a rolling deploy briefly runs two Api instances, they
  cannot corrupt each other's migration run.

### The decision

**Keep in-process startup migration, hardened** — it matches the Coolify
workflow (stop, redeploy, service brings the schema up) and a single-instance
deployment. The alternative — a `dotnet ef migrations bundle` executed as a
separate pre-start step with `SkipStartupMigration=true` on the app — is
operationally cleaner for multi-instance setups but adds Coolify plumbing for
no benefit today. It remains the documented escape hatch if the deployment
ever grows a second Api instance.

## Design

### Startup migrator (replaces both steps of today's path)

A small `StartupMigrator` in `application/Hannibal/Data/`, called from
`Api/Program.cs` under the existing flag:

- **Wait, then migrate.** Retry loop with a 60-second deadline (same shape as
  `HannibalContext.cs:39-75`): catch only transient connectivity errors
  (`NpgsqlException` / `SocketException`); anything else — a real migration
  failure — rethrows immediately with the connection *source* named (the
  resolution logging in `DependencyInjection.cs` already knows it).
- **Say what it is doing.** Log the pending-migration list before applying,
  and the applied list after. A deploy that migrates silently is a deploy that
  fails undiagnosably.
- **Refuse the poisoned state explicitly.** If the database has tables but no
  `__EFMigrationsHistory`, fail with a message pointing at the baseline
  runbook (Gate M2) instead of the raw `relation already exists` error.
- **`EnsureCreatedAsync` and the seeder are deleted.** `Migrate()` creates the
  database if missing; nothing else may. `InitializeDatabaseAsync` and
  `_createDevContent` go away entirely (production data seeded long ago is
  data, not code — deleting the seeder deletes nothing).

### Baseline runbook for the production database (one-time)

Executed manually against the live DB, before the hardened code deploys:

1. **Backup:** `pg_dump -U postgres -Fc hannibal > hannibal-$(date +%F).dump`
   inside the `hannibal-db` container.
2. **Probe:**
   ```sql
   SELECT EXISTS (SELECT 1 FROM information_schema.tables
                  WHERE table_name = '__EFMigrationsHistory') AS has_history;
   ```
   If `has_history` is true and `SELECT * FROM "__EFMigrationsHistory"` lists
   all six migrations — nothing to do, the failures were defect 2 or 3.
3. **If history is missing**, determine how much schema is present:
   ```sql
   SELECT EXISTS (SELECT 1 FROM information_schema.tables
                  WHERE table_name = 'oauth_state') AS m2;
   SELECT column_name FROM information_schema.columns
   WHERE table_name = 'Storages'
     AND column_name IN ('ExpiresAt','ClientId','ClientSecret',
                         'Username','Password','Host','Port','Domain');
   ```
   Marker → migration mapping (each migration's own additions):

   | Marker | Migration |
   |---|---|
   | any table at all | `20260101121727_AddedOAuth2Email` (full schema) |
   | `oauth_state` table | `20260104044852_AddOAuth2ToState` |
   | `Storages.ExpiresAt` | `20260110090737_AddExpiresAt` |
   | `Storages.ClientId` | `20260110120829_AddClientId` |
   | `Storages.ClientSecret` | `20260111071806_AddClientSecretToStorage` |
   | `Storages.Username/Password/Host/Port/Domain` | `20260120100000_AddCredentialFieldsToStorage` |

4. **Baseline:** create the history table and insert one row per migration
   whose markers are present (`ProductVersion` is informational; use the EF
   version, currently `9.0.5` per `application/Hannibal/Hannibal.csproj`):
   ```sql
   CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
       "MigrationId"    character varying(150) NOT NULL,
       "ProductVersion" character varying(32)  NOT NULL,
       CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
   );
   INSERT INTO "__EFMigrationsHistory" VALUES
     ('20260101121727_AddedOAuth2Email', '9.0.5'),
     ('20260104044852_AddOAuth2ToState', '9.0.5')
     -- ... up to the last migration whose markers exist
   ON CONFLICT DO NOTHING;
   ```
   Migrations *after* the baseline point are then applied normally by the next
   deploy. (A schema created by `EnsureCreated` can differ in incidentals from
   one built by migrations; the markers above are the load-bearing parts.)

### Deployment ordering guard

`docker-compose.yml`: give `api` a `depends_on: hannibal-db:
condition: service_healthy` — the healthcheck already exists. The in-code
retry stays regardless; the compose guard just makes the common case quiet.

### Keeping model and migrations in lockstep

The failure mode "model changed, nobody ran `dotnet ef migrations add`" is
currently invisible until a deploy. EF 9's
`context.Database.HasPendingModelChanges()` compares the model against the
migration snapshot **without touching a database** — a plain unit test in
`tests/Hannibal.Tests/` asserts it is false, so a forgotten migration fails
the suite on every PR.

Authoring discipline, recorded in `CLAUDE.md` and `docs/TESTING.md`:

- Every schema change ships as a migration in the same PR
  (`dotnet ef migrations add <Name> --project application/Hannibal/`).
- Migrations are forward-only in production; recovery is restore-from-dump,
  which is why the pre-deploy `pg_dump` is part of the deploy runbook, not
  optional.
- `EnsureCreated` never returns, in any code path.

## Gates

### Gate M1 — Hardened startup migrator — **MET (2026-08-21)**

`application/Hannibal/Data/StartupMigrator.cs`, called from the
`Hannibal:SkipStartupMigration`-gated block in `Api/Program.cs`.

**Acceptance**
1. `StartupMigrator` retries transient connection failures up to a deadline
   and then fails with the connection source named; a genuine migration error
   is not retried. ✔ — the transient/missing-database classification is pure
   (`_isTransient` / `_isMissingDatabase`) and unit-tested
   (`StartupMigratorClassificationTests`); the deadline is pinned by a test
   against an unreachable port.
2. Pending migrations are logged before applying; the applied set after. ✔
3. A database with tables but no `__EFMigrationsHistory` produces the
   explicit baseline-runbook error, not `relation already exists`. ✔ — pinned
   by `StartupMigratorTests.ADatabaseWithoutMigrationHistory_...`, which
   builds exactly that state with `EnsureCreated` in a throwaway sibling
   database. **Finding:** `GetAppliedMigrationsAsync` cannot be used as the
   reachability probe — EF's history repository swallows a *missing database*
   into "no migrations applied", indistinguishable from the poisoned state;
   the migrator therefore probes with an explicit connection open, which does
   surface `3D000`.
4. `InitializeDatabaseAsync`, `_createDevContent` and the `!!` seed condition
   are gone; nothing in the solution calls `EnsureCreated*` outside tests. ✔
5. The `HasPendingModelChanges` test
   (`MigrationsCoverModelTests.EveryModelChangeHasAMigration`, no database
   needed) goes red when a model property is added without a migration —
   demonstrated with a temporary `Storage` property, then reverted. ✔
6. Full suite green: 42 + 63(+1 skip) + 9 + 29 + 13 + 4 = **160 passed,
   1 skipped, 161 total** (up from `main`'s 152/1/153; the parallel
   skip-job-acquisition PR adds two more). ✔

### Gate M2 — Production baselined

**Diagnosis result (2026-08-21, run by Timo on the live `hannibal` DB).**
`__EFMigrationsHistory` **exists** and lists **all six** migrations, each at
`ProductVersion` 9.0.5; `\dt` shows the complete schema (12 app/Identity
tables plus `oauth_state`). Production is fully migrated — **the poisoned
`EnsureCreated` state (defect 1) is not present and no baseline SQL is
needed.** Steps 3–4 of the runbook are moot. By elimination, the past deploy
unreliability points at defect 2 (the `Migrate()`-before-Postgres-ready race,
`Api/Program.cs:807` with no retry and no compose `depends_on`), which Gates
M1 and M3 close. Defect 1 remains worth fixing in M1: the `EnsureCreatedAsync`
path is still in the code and would poison any *future* fresh environment
that starts with `SkipStartupMigration` semantics confused.

**Acceptance**
1. `pg_dump` backup taken and its restorability spot-checked
   (`pg_restore --list`).
2. Probe results recorded in this file. ✔ (above)
3. ~~Baseline insert~~ — not needed, history is complete. ✔
4. A redeploy of the current image starts cleanly; the log shows zero pending
   migrations.

### Gate M3 — Deploy ordering — **code MET (2026-08-21), one observation open**

**Acceptance**
1. `api` waits for `hannibal-db` health in `docker-compose.yml`. ✔
2. ⏳ One full Coolify redeploy observed: no connection-refused crash loop,
   Api healthy on first start. Shared with Gate M2 AC4 — the next deploy
   closes both.

### Gate M4 — Discipline recorded

**Acceptance**
1. `CLAUDE.md` and `docs/TESTING.md` carry the authoring rules and the deploy
   runbook (backup → deploy → verify log line).
2. The migration-only rule has its enforcement test (M1.5) referenced there.

## Verification

```bash
dotnet build Backer.sln
dotnet test tests/Hannibal.Tests/               # includes HasPendingModelChanges
dotnet test tests/Hannibal.IntegrationTests/    # includes the poisoned-state test
dotnet test tests/Backer.E2ETests/
```

Standing constraints: no test touches a live database — the poisoned-state
test runs against `PostgresFixture`'s throwaway database like every other
integration test.
