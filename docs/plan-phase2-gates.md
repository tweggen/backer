# Phase 2 — Test Harness: Gates & Acceptance Criteria

Companion to `docs/plan-onedrive-oauth2-reauth.md` §"Phase 2". Each gate is
independently verifiable. A gate is **met** only when every acceptance
criterion below it is demonstrably true (command output, not assertion).

Baseline before this work: 28 tests, all pure unit, in `tests/Hannibal.Tests`
(11) and `tests/WorkerRClone.Tests` (14 + 3). No DB harness, no API host
harness, no mocking library, no OAuth2 seam.

Constraint honoured throughout: **no test may touch live backup data, start
rclone, or write to a real cloud account.** The only exception is Gate F,
which is opt-in via environment variable and read-only.

---

## Gate A — Host is testable

Make `Api` hostable under `WebApplicationFactory` without changing its
production behaviour.

Changes:
- `Api/Program.cs`: add `public partial class Program { }`; replace
  `await app.StartAsync(); await app.WaitForShutdownAsync();` with `app.Run()`.
- `application/Hannibal/DependencyInjection.cs`: resolve the connection string
  as `IConfiguration["ConnectionStrings:DefaultConnection"]` →
  `HANNIBAL_DB_CONNECTION` env var → existing hardcoded fallback, in that
  order. Remove the `Console.WriteLine` that prints the connection string
  (it contains the DB password). Keep the env var working — it is what the
  live deployment uses.
- Startup migration/seed block guarded so a test host can opt out
  (`Hannibal:SkipStartupMigration` config flag), and
  `HannibalContext.InitializeDatabaseAsync`'s unbounded
  `while (!haveDatabase)` retry loop bounded with a timeout so a bad
  connection fails fast instead of hanging forever.

**Acceptance**
1. `dotnet build Backer.sln -c Debug` succeeds with no new warnings.
2. `dotnet test tests/Hannibal.Tests tests/WorkerRClone.Tests` — 28/28 pass.
3. `dotnet run --project Api/` against the local Postgres still starts,
   migrates, and serves `/health` (manual smoke, recorded in this doc).
4. Setting `ConnectionStrings:DefaultConnection` overrides the env var;
   with neither set, the hardcoded fallback is still used.
5. No connection string (and therefore no DB password) is written to stdout.

---

## Gate B — Throwaway Postgres fixture

New project `tests/Hannibal.IntegrationTests` with an xUnit
`ICollectionFixture` that creates a uniquely-named database per run, applies
`Database.Migrate()`, and drops it on dispose.

- Admin connection resolved from `BACKER_TEST_DB_CONNECTION`, else
  `Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=admin`.
- DB name `backer_test_{yyyyMMddHHmmss}_{8 hex}` so parallel/aborted runs
  never collide.
- Drop uses `WITH (FORCE)` so lingering pooled connections cannot block it;
  `NpgsqlConnection.ClearAllPools()` before drop.
- If Postgres is unreachable, the whole collection is **skipped with a clear
  message**, not failed — a dev without Postgres must still get a green
  `dotnet test`.
- Per-test isolation: each test gets a fresh `HannibalContext` scope and
  truncates the tables it dirties (no cross-test leakage).

**Acceptance**
1. A smoke test writes a `Storage` row, reads it back through a second
   `HannibalContext`, and asserts equality — passes.
2. All 6 migrations apply cleanly to an empty DB (fixture asserts
   `GetPendingMigrations()` is empty after `Migrate()`).
3. After the run, `SELECT datname FROM pg_database WHERE datname LIKE
   'backer_test_%'` returns zero rows — verified by a follow-up query.
4. With Postgres stopped (or a bogus `BACKER_TEST_DB_CONNECTION`), the run
   reports the tests as *skipped*, exit code 0, and the skip reason names the
   connection target.
5. The existing `hannibal` database is never opened, written, or dropped.

---

## Gate C — API host harness + endpoint integration tests

`Microsoft.AspNetCore.Mvc.Testing`-based `BackerApiFactory :
WebApplicationFactory<Program>` wired to the Gate B database.

The factory must:
- point the app at the fixture DB via in-memory config;
- **remove the `RuleScheduler` hosted service** (it creates `Job` rows on a
  background loop with job creation enabled — non-deterministic tests);
- supply deterministic `Jwt:Key/Issuer/Audience` and stub
  `OAuth2:Providers:*` credentials, overriding the real user-secrets that
  `Program.cs:25` loads;
- replace `IHubContext<HannibalHub>` with a recording fake so SignalR
  broadcasts are assertable without a live hub connection;
- disable HTTPS redirection for the in-memory client.

Tests:
- `POST /api/authb/v1/token` — valid credentials return a JWT that validates
  against the configured key and carries `sub`/`email` claims; wrong password
  returns 401; unknown user returns 401.
- `POST .../users/triggerOAuth2` — 401 without a bearer token; with a token,
  returns a Microsoft authorize URL and persists exactly one `OAuthState` row
  whose `Provider`/`ReturnUrl`/`UserId` match the request and whose `Used` is
  false.
- `GET .../users/processOAuth2Result` — unknown state, already-`Used` state,
  and >10-minute-old state are each rejected rather than silently succeeding;
  `error=access_denied` in the query is echoed back in
  `ProcessOAuth2Result.Error`.
  **Corrected after implementation:** this criterion originally said these
  cases "surface an error rather than a 500". They do not. All three throw
  `UnauthorizedAccessException("State not found")` from
  `HannibalService.cs:154-157`, which `Api/Program.cs:296-299` catches into a
  bare `500` with an empty body. The tests assert the *observed* behaviour and
  document the gap. Turning that into a structured error is a product change,
  not test infrastructure — see "Findings for Phase 3" below.

**Acceptance**
1. All Gate C tests pass against the fixture DB.
2. **Regression proof for Phase 1 fix 4**: a test asserts that persisting
   OAuth tokens through the self-update path fires exactly one
   `StorageReauthenticated` broadcast carrying the storage's `UriSchema`.
   This test must be demonstrated to **fail** when the `isSelfUpdate` guard
   at `HannibalServiceStorages.cs:130-138` is reverted, and pass with it.
   The demonstration is recorded in this document.
3. No `Job` rows are created during any integration test run (asserted).
4. Test run does not read the developer's real user-secrets values into any
   outbound request.

---

## Gate D — OAuth2 seam + fake authorization server

- Extract `IOAuth2ClientFactory`; register it in DI; inject it into
  `HannibalService` (replacing `new OAuth2ClientFactory(...)` at
  `HannibalService.cs:62`). `WorkerRClone/DependencyInjection.cs:24` updated
  to register against the interface.
- Make the redirect URI configurable (`OAuth2:RedirectUri`, default
  `http://localhost:53682/` — unchanged behaviour when unset), removing the
  three hardcoded copies in `OAuth2ClientFactory.cs`.
- Allow an `IRequestFactory` to be supplied to `OAuth2ClientFactory` so the
  RestSharp transport can be pointed at a stub, following the submodule's
  existing NSubstitute precedent (`OAuth2.Tests/Client/OAuth2ClientTests.cs`).
- Add NSubstitute to the test projects (same library the submodule already
  uses).

Tests over the full code-for-token exchange:
- happy path: token endpoint returns tokens, `/me` returns a full profile →
  `Storage` gets `AccessToken`/`RefreshToken`/`ExpiresAt` persisted;
- **`invalid_client`** (the exact live failure: expired client secret) → the
  exchange fails, the error is *logged*, and `ProcessOAuth2Result.Error`
  contains the provider's message rather than a bare
  "Unable to read user info";
- `/me` response missing `givenName`/`surname` (MSA account) → no
  `NullReferenceException`, user info parses, exchange succeeds;
- state mismatch / email mismatch → `UnauthorizedAccessException` path.

**Acceptance**
1. `grep -n "new OAuth2ClientFactory" application/ worker/` returns only the
   DI registration.
2. All four scenarios above pass as automated tests, with no network access
   (verified by pointing the stub at an unroutable host — a real call would
   fail the test).
3. With `OAuth2:RedirectUri` unset, the generated authorize URL still
   contains the encoded `http://localhost:53682/` — proving the production
   flow is unchanged. **Note:** RestSharp 106.12 percent-encodes with
   *lowercase* hex, so the emitted form is
   `redirect_uri=http%3a%2f%2flocalhost%3a53682%2f`, not the uppercase
   `%3A%2F%2F` this line originally predicted. Assertions are
   case-insensitive.
4. The `invalid_client` test asserts on captured `ILogger` output, so a
   regression back to silent failure breaks the build.

---

## Gate E — Handler unit tests + token-leak removal

- Delete the two token-printing statements: `Tools/AddTokenHandler.cs:21`
  (`Console.WriteLine($"[AddTokenHandler] Token: {token}")`) and
  `Tools/AutoAuthHandler.cs:58` (`"Set new jwt token: {newToken}"`).
  Replace with `ILogger` at Debug that logs *presence and length*, never the
  value.
- Fix `AutoAuthHandler.CloneHttpRequestMessageAsync` (`:80-92`): buffer the
  request content before the first send and rebuild it for the retry, so a
  401-retry on a request with a body does not throw
  `ObjectDisposedException`/"request message was already sent". Copy content
  headers too.
- Remove the dead never-assigned fields (`_authEndpoint`, `_username`,
  `_password`) and the accepted-but-discarded `HttpClient authClient`
  constructor parameter, updating `BackerAgent/DependencyInjection.cs:36`.

Tests in a new `tests/Tools.Tests`, driven by a fake inner
`HttpMessageHandler`:
- `AddTokenHandler` attaches `Authorization: Bearer <token>`; attaches
  nothing when the provider returns null/empty.
- `AutoAuthHandler`: 200 passes through untouched; 401 → token refresh →
  exactly one retry with the *new* token; refresh returning an empty token
  yields 401 without a retry; a 401 retry on a `POST` **with a body**
  succeeds and the inner handler observes the body both times (regression
  test for the rebuffering fix); the retry is not attempted twice.

**Acceptance**
1. `grep -rn "Console.WriteLine" Tools/` shows no statement that prints a
   token or JWT.
2. `grep -rn "LoggingFields.All" BackerAgent/` and the
   `RequestHeaders.Add("Authorization")` at `BackerAgent/Program.cs:104-108`
   are reported (flagged for Phase 3 if not removed here).
3. The body-rebuffering test fails against the pre-fix handler and passes
   after — demonstrated and recorded.
4. All `tests/Tools.Tests` pass; `BackerAgent` still builds and starts.

---

## Gate F — Opt-in live credential check (read-only)

- Refactor `OneDriveProvider.GetDriveInfoAsync`
  (`worker/WorkerRClone/Services/Providers/OAuth/OneDriveProvider.cs:55-79`)
  to obtain its `HttpClient` from an injected `IHttpClientFactory`, keeping a
  default so existing DI registration continues to work.
- Add an offline unit test for `GetDriveInfoAsync` against a stub handler:
  correct `Authorization` header, correct `/v1.0/me/drive` path, parses
  `id`/`driveType`, throws a *descriptive* error on 401 rather than a bare
  `HttpRequestException`.
- Add an env-gated harness test (`BACKER_LIVE_OAUTH_TEST=1`, plus a storage
  id) that runs `EnsureTokensValidAsync` followed by a read-only
  `GET /me/drive` against the real account and reports token validity.
  Skipped by default.
- Remove the refresh-token value from
  `OAuthStorageProviderBase.cs:85`'s debug log.

**Acceptance**
1. With `BACKER_LIVE_OAUTH_TEST` unset, the live test reports *skipped* and
   makes zero network calls.
2. The offline `GetDriveInfoAsync` tests pass with no network access.
3. No code path in Gate F starts rclone, writes to a remote, or mutates any
   `Storage` row other than the token refresh that
   `EnsureTokensValidAsync` already performs in production.
4. `grep -n "RefreshToken" worker/WorkerRClone/Services/Providers/OAuth/OAuthStorageProviderBase.cs`
   shows no token *value* being logged.

---

## Gate G — Wiring & documentation

**Acceptance**
1. `tests/Hannibal.IntegrationTests` and `tests/Tools.Tests` are in
   `Backer.sln` under the existing `Tests` solution folder.
2. A single `dotnet test Backer.sln` runs every test project; total count and
   pass/skip breakdown recorded in this document.
3. `CLAUDE.md`'s test commands updated (it currently names only
   `tests/WorkerRClone.Tests` and predates `Hannibal.Tests`).
4. `docs/TESTING.md` written: how to run unit vs integration tests, the
   Postgres prerequisite and how to skip it, and how to opt into the live
   OneDrive check.

---

## Verification log

Completed 2026-08-02.

| Gate | Status | Evidence |
|------|--------|----------|
| A | **met** | `dotnet build Backer.sln` 0 errors; 102 distinct warnings before and after the change, `diff` empty. API started against a throwaway `backer_smoke_api` DB, applied all 6 migrations, `GET /health` → 200 `Healthy`. Config precedence proven by 5 automated tests plus a live run where `HANNIBAL_DB_CONNECTION` held a decoy password: stdout showed `Using database localhost:5432/backer_smoke_api (from ConnectionStrings:DefaultConnection)` and 0 occurrences of any password. `SkipStartupMigration=true` verified against an empty DB: 0 "Applying migration" lines, no `__EFMigrationsHistory`. |
| B | **met** | Postgres 17.5 reachable on localhost:5432 as `postgres/admin`. Smoke test writes a `Storage` and reads it back through a second `HannibalContext`. `GetPendingMigrations()` empty after `Migrate()`. Post-run `SELECT datname FROM pg_database WHERE datname LIKE 'backer_test_%'` → **0 rows** (independently re-verified by the orchestrator); `hannibal` intact with all 6 migrations. With a bogus `BACKER_TEST_DB_CONNECTION`: 21 skipped, exit code **0**, reason names host/port/db and no password. Uses `Xunit.SkippableFact` 1.5.23; the fixture *records* unavailability rather than throwing, because a throwing `InitializeAsync` reports the collection as failed, not skipped. |
| C | **met** (one criterion unmet by the product — see below) | 26/26 pass. Regression proof recorded below. No `Job` rows created: asserted two ways — no `IHostedService` resolves to `RuleScheduler`, and `ResetAsync` asserts `Jobs.Count == 0` *before* truncating, so a job leaked by any test fails the next one. No real secret leaves the process: asserted inside the running host that `OAuth2:Providers:onedrive:ClientId == "integration-test-onedrive-client-id"`, closed loop via the outgoing authorize URL, with a `PostConfigure<OAuthOptions>` as belt-and-braces. |
| D | **met** | `grep -rn "new OAuth2ClientFactory" application/ worker/` → only the two DI registrations. 15 new tests. Offline by construction plus a tripwire test proving no code path creates its own transport behind the seam. The `invalid_client` test asserts on captured `ILogger` output (exactly one `LogLevel.Error` naming provider, state id, user id and `invalid_client`), so a regression to silent failure breaks the build. Redirect-URI default asserted case-insensitively. |
| E | **met** | 9/9 pass. Rebuffering fix demonstrated failing then passing (below). Tools warnings 26 → 17, all 9 removed from `AutoAuthHandler.cs`. `grep -rn "Console.WriteLine" Tools/` shows only network-interface diagnostics and one commented-out line — no token or JWT. |
| F | **met** (live path unexercised) | 16 offline Graph tests pass, zero network by construction. Live check skips with `BACKER_LIVE_OAUTH_TEST` unset, exit 0. Gate verified in *both* directions: with all three vars set it actually runs and fails at `NpgsqlConnector.ConnectAsync` against a deliberately unroutable DB, proving it is wired to real work and does not fake success. No token value logged in `OAuthStorageProviderBase`. |
| G | **met** | `dotnet test Backer.sln` → **103 passed, 1 skipped, 104 total** across 4 projects (Tools.Tests 9, Hannibal.Tests 37, WorkerRClone.Tests 31+1, Hannibal.IntegrationTests 26); independently re-run by the orchestrator. Both new projects in `Backer.sln` under the `Tests` folder. `CLAUDE.md` updated; `docs/TESTING.md` written. |

Baseline before this work: 36 tests (Hannibal.Tests 22, WorkerRClone.Tests 14).
The plan's "28 tests" figure counted test *methods*, not theory cases.

### Gate C acceptance #2 — Phase 1 regression proof

The test seeds `Storage(Technology="onedrive", OAuth2Email="reauth-user@example.com",
UriSchema="RegressionOneDrive")` plus a matching fresh `OAuthState`, stubs
`/token` and `/me` through `IRequestFactory`, and calls the real
`GET /processOAuth2Result`. It asserts the tokens were actually persisted, the
state was marked `Used`, and that exactly one broadcast reached `All` with the
single argument `"RegressionOneDrive"`.

With the `isSelfUpdate` guard at `HannibalServiceStorages.cs:96,130-138`
temporarily reverted (19 lines deleted):

```
Error Message:
 Expected broadcasts to contain a single item because persisting OAuth2 tokens
 through the self-update path must notify the agents exactly once - without the
 isSelfUpdate guard there is no broadcast at all. Recorded: [], but the
 collection is empty.

Failed!  - Failed: 2, Passed: 0, Skipped: 0, Total: 2
```

With the guard restored:

```
Passed!  - Failed: 0, Passed: 2, Skipped: 0, Total: 2, Duration: 896 ms
```

The file was restored via `git checkout --`; `git hash-object` is
`f4d425728a72c9714dc54c589341aa3a26a8ba44` before and after, and the file does
not appear in `git diff --stat`.

### Gate E acceptance #3 — rebuffering proof

Worth recording because the obvious version of this test is useless: with
`StringContent` the test **passes even against the unfixed handler**, because
`ByteArrayContent`-derived content is rewindable and serializes twice happily.
The bug only reproduces with a genuinely non-rewindable body, so the test uses
`StreamContent` over a forward-only stream and the fake inner handler
serializes with `CopyToAsync` rather than the buffering `ReadAsByteArrayAsync`.
Both variants are kept.

Pre-fix:

```
Tools.Tests.AutoAuthHandlerTests.Unauthorized_RetryOnPostWithNonRewindableBody_SendsTheSameBodyTwice [FAIL]
 System.InvalidOperationException : The stream was already consumed. It cannot be read again.
   at System.Net.Http.StreamContent.PrepareContent()
   at Tools.AutoAuthHandler.SendAsync(...) in ...\Tools\AutoAuthHandler.cs:line 71
Failed!  - Failed: 1, Passed: 8, Skipped: 0, Total: 9
```

Post-fix (`LoadIntoBufferAsync` + fresh `ByteArrayContent` with copied content
headers):

```
Passed!  - Failed: 0, Passed: 9, Skipped: 0, Total: 9
```

---

## Findings for Phase 3

Behaviour the tests now pin down and document, but deliberately did **not**
change — each is a product decision, not test infrastructure.

1. **`processOAuth2Result` returns a bare 500 with an empty body** for an
   unknown, already-used, or expired OAuth state. The exception is thrown at
   `HannibalService.cs:154-157` and flattened by the catch-all at
   `Api/Program.cs:296-299`. This is the same class of silent failure Phase 1
   set out to fix, one layer up.
2. **`triggerOAuth2` maps every exception to 404** (`Api/Program.cs:267-270`),
   so an unknown provider is indistinguishable from an internal fault.
3. **The email-mismatch check is inside the try block**
   (`HannibalService.cs:179`), so `UnauthorizedAccessException("User id
   mismatch")` is swallowed by the catch and surfaces as
   `Error = "Unable to read user info: User id mismatch"` with HTTP 200,
   rather than as a rejection. Decide whether that is intended.
4. **`BackerAgent/Program.cs:103-109` logs the full `Authorization` header** —
   `AddHttpLogging` with `LoggingFields.All` plus an explicit
   `logging.RequestHeaders.Add("Authorization")`. That is the bearer JWT, plus
   full request and response bodies, whenever HTTP logging runs at Information
   level. Left alone as it may be deliberate for debugging, but it belongs in
   the Phase 3 security section.
5. **`Api/Program.cs:104` sets `ValidateLifetime = false`** with a
   `// TXWTODO: This is bad` comment — expired JWTs are accepted.
6. **`OAuthStorageProviderBase.cs:33`** passes `new Guid()` (i.e.
   `Guid.Empty`) as the PKCE state id. Harmless today only because
   `MicrosoftGraphClient._usePkce == false`.
7. **No CI.** `.github/workflows/` is empty. `BackerControl` is
   `net9.0-windows`, so a Linux runner cannot build `Backer.sln` whole and
   would need a filtered project list. `Directory.Build.props` is untracked,
   so a fresh clone builds without it.
