# End-to-end test harness

Status: planned, not yet executed.

## Context

Before the git-storage work (`docs/plan-git-repo-storage.md`) adds a *second*
transfer engine, there needs to be a harness that can prove a job travels the
whole way: browser → Poe → Api → PostgreSQL → SignalR → BackerAgent →
transfer engine → report → PostgreSQL. Today half of that path has no
automated coverage at all, and the half that does is missing its two most
important behaviours.

### Key facts (verified 2026-08-20 against `3d9d354`)

What already exists, and is good:

- `tests/Hannibal.IntegrationTests` hosts the real `Api` under
  `WebApplicationFactory<Program>` against a throwaway PostgreSQL database:
  `tests/Hannibal.IntegrationTests/BackerApiFactory.cs`,
  `PostgresFixture.cs`, `ApiIntegrationTestBase.cs`. Real HTTP, real EF, real
  DB, skipping cleanly when PostgreSQL is absent
  (`ApiIntegrationTestBase.ArrangeAsync`).
- `Api/Program.cs:821` already carries `public partial class Program` — the
  hook `WebApplicationFactory` needs.
- 28 tests across 7 classes (5 `[Fact]`, 23 `[SkippableFact]`), covering
  token issuance, OAuth2 trigger/callback, storage-reauth broadcast, host
  isolation and connection-string resolution.

The two exclusions that matter:

- **Job creation is switched off in the harness.** `BackerApiFactory` removes
  the `RuleScheduler` `IHostedService` descriptor
  (`tests/Hannibal.IntegrationTests/BackerApiFactory.cs:116`, implemented at
  `:208-248`) because with job creation enabled it writes `Job` rows on a
  background loop. So the central business behaviour — a `Rule` producing
  `Job`s — has zero integration coverage.
- **SignalR is faked.** `IHubContext<HannibalHub>` is replaced by
  `RecordingHubContext` (`BackerApiFactory.cs:121-122`). Tests assert
  broadcast *intent*; no client ever receives a message.

What has no coverage at all:

- **The whole agent.** There is no in-process `BackerAgent` harness. Three
  concrete blockers:
  1. `BackerAgent/Program.cs` has no `public partial class Program` (only
     `Api/Program.cs:821` does) and ends with
     `await app.StartAsync(); await app.WaitForShutdownAsync();` — exactly the
     pattern `docs/plan-phase2-gates.md` Gate A had to replace in `Api` with
     `app.Run()` to make it hostable.
  2. `IRCloneClient` (`worker/WorkerRClone/Client/IRCloneClient.cs:3`) exists
     but is **never used**: all seven construction sites do
     `new RCloneClient(httpClient)` directly —
     `worker/WorkerRClone/Services/RCloneService.cs:289, 783, 1041, 1388,
     1438, 1840, 2001`. There is no injection seam.
  3. The rclone RC endpoint is effectively hardcoded. `_defaultRCloneUrl =
     "http://localhost:5572"` (`RCloneService.cs:81`) is what both
     `_checkRCloneProcessImpl` (`:1210`) and `_startRCloneProcessImpl`
     (`:1237`) pass to `_haveRCloneProcess`, and `RCloneServiceOptions`
     (`worker/WorkerRClone/Configuration/RCloneServiceOptions.cs`) has no URL
     property. Worse, `_startRCloneProcess` parses the real URL out of
     rclone's stderr into a local variable and then **discards it**
     (`RCloneService.cs:1017-1025`) — the assignment is dead. Any agent test
     today would therefore launch a real rclone process.
- **The Blazor frontend.** No test project for `frontend/Poe`, no bUnit, no
  browser automation. Poe authenticates by cookie
  (`frontend/Poe/Program.cs:49-59`) and calls the API through a named
  `AuthenticatedClient` with an `IdentityCookieHandler`
  (`frontend/Poe/Program.cs:37-47`), so it is a genuinely separate process
  from `Api`.

Facts that make the harness cheaper than it looks:

- **rclone's RC interface is plain HTTP + JSON.** `RCloneClient` talks to it
  over an `HttpClient` whose `BaseAddress` is set in `_haveRCloneProcess`
  (`RCloneService.cs:1035`), with hardcoded basic auth `who:how`
  (`RCloneService.cs:1036-1039`). A stub HTTP server can therefore
  impersonate rclone *completely* — no process, no real file transfer, and
  full control over timing and failure injection.
- **The scheduler is event-driven, not purely timer-driven.**
  `RuleScheduler.ExecuteAsync` waits on a `SemaphoreSlim _wakeupSignal` with a
  timeout and then calls `ProcessReadyRulesAsync` +
  `ProcessPendingEventsAsync`
  (`application/Hannibal/Services/Scheduling/RuleScheduler.cs:109-171`).
  `PublishEventAsync` (`:84`) and `SetJobCreationEnabled(bool)` (`:102`) are
  public. A test can seed DB state and publish an event to force a
  deterministic scheduler pass instead of sleeping.
- **`ScheduleCalculator` already takes `now` as a parameter** —
  `CalculateNextExecution(rule, state, DateTime now)` (`:31`) and
  `IsReadyToExecute(rule, state, now)` (`:120`). Only `RuleScheduler` reads
  `DateTime.UtcNow` directly (`:132, 218, 338, 341, 412, 472, 475, 487, 510,
  529`), so a clock seam there is small and local.
- `docker-compose.yml` at the repo root is the **production** deployment
  (Hetzner volume path, `essentialvault.de` Traefik labels, a committed
  database password). It is not usable as a local test stack; the browser
  layer needs its own compose file. (The committed credential is a known
  issue tracked separately in the secrets-removal draft; this plan does not
  touch it, but the new compose file must not repeat the pattern.)

### Scope decided with Timo (2026-08-20)

Reach the **full stack including real-browser tests**, and cover all four
areas: the job pipeline, failure/re-auth paths, concurrency/safety, and
config export/import.

## Design

### Layers

```
L5  Playwright ──▶ Poe ──▶ Api ──▶ Postgres        (real processes, real browser)
                            │
                            └──▶ BackerAgent ──▶ stub rclone

L4  full-loop tests: Api (in-memory) + BackerAgent (in-memory) + stub rclone
L3  agent host harness: BackerAgent under WebApplicationFactory
L2  stub rclone RC server: impersonates rclone over HTTP
L1  Api integration harness (exists): BackerApiFactory + PostgresFixture
L0  unit tests (exists): 78 tests, no I/O
```

L1–L4 run in one process with an in-memory `TestServer` and are the day-to-day
regression suite. L5 launches real processes and is the pre-release suite.
Each layer is usable without the ones above it.

### The stub rclone (L2)

A small ASP.NET Core host implementing the subset of rclone's RC API that
`RCloneClient` actually calls — verified by reading every method of
`worker/WorkerRClone/Client/RCloneClient.cs`, which is exactly eleven paths:
`config/listremotes`, `config/create`, `config/paths`, `sync/copy`,
`sync/sync`, `rc/noop`, `job/status`, `job/stop`, `job/list`, `core/stats`,
`core/quit`. It accepts the hardcoded `who:how` basic auth
(`RCloneService.cs:1036-1039`).

It is **scriptable**, because every interesting regression in this codebase is
a failure path:

- complete a job after N polls, or never;
- fail a job with a chosen error;
- stop a job and report it as cancelled;
- report arbitrary `core/stats` so transfer-progress plumbing can be asserted;
- record every request so tests assert on *what the agent asked rclone to do*
  — the remote config written, the source and destination URIs — which is the
  real contract between Backer and rclone;
- expose arrival of a request as an awaitable condition, so tests never sleep.

**What the stub deliberately cannot do.** The agent's token-error detection
reads the *stderr of the rclone process* (`_readPrintLog`,
`RCloneService.cs:911`, feeding `_stderrTokenErrorCount`,
`RCloneService.cs:59-60`), and the OAuth2 inactivity timeout is driven from
the same process plumbing (`RCloneService.cs:67`). An HTTP stub has no stderr.
Those two paths therefore need the *process* seam, not the RC seam, and are
covered in Gate 2 (which owns process start) rather than here.

**Fidelity is asserted, not verified.** The stub's payloads are shaped to what
`RCloneClient` reads. That makes it a valid double, but does not prove the
field names match a real rclone — most sharply for `job/list`, where
`JobListResult` (`worker/WorkerRClone/Client/Models/JobListResult.cs`) declares
`jobsids`, `running_ids` and `finished_ids` and the production stop path reads
`running_ids` (`RCloneService.cs:1390-1394`). The stub emits those spellings
without having confirmed them against rclone, and emits only one spelling each
so the open question stays visible. Closing it needs an opt-in test against a
real rclone binary, in the style of the existing live OneDrive check — added
as a follow-up in Gate 10, not silently assumed here.

Because the stub records the `sync/copy` and `sync/sync` calls, L4 tests can
assert the exact `remote:/path` URIs built at `RCloneService.cs:700` without
moving a byte.

### Production seams required

Small, named, and each one gated. Nothing else in production code changes.

1. `BackerAgent/Program.cs`: add `public partial class Program { }` and
   replace `await app.StartAsync(); await app.WaitForShutdownAsync();` with
   `app.Run()`. Identical to the change `Api` already received.
2. `RCloneServiceOptions`: add `RCloneUrl` (nullable). `RCloneService` uses it
   in place of `_defaultRCloneUrl` at `:1210` and `:1237`, falling back to the
   current constant when unset. While there, make `_startRCloneProcess`
   actually *use* the URL it parses instead of discarding it
   (`RCloneService.cs:1017-1025`) — that is a live bug, not just a test
   inconvenience: an rclone that binds a non-default port is currently
   unreachable.
3. `RCloneServiceOptions`: add `SkipProcessStart` (default false) so the
   harness can point the agent at the stub without any chance of spawning
   rclone. Belt to `RCloneUrl`'s braces.
4. `BackerApiFactory`: turn its two blanket substitutions into **modes** —
   `SchedulerMode.{Removed, Enabled}` and `HubMode.{Recording, Real}` —
   defaulting to today's behaviour so the existing 28 tests are untouched.
   Full-loop tests select `Enabled` + `Real`.
5. `RuleScheduler`: inject `TimeProvider` (built into .NET 9) and replace the
   ten `DateTime.UtcNow` reads. `ScheduleCalculator` needs no change — it
   already receives `now`. Deferred to Gate 4, where it is actually needed.
6. `ConfigHelper<TOptions>`: optional `configDirectory` parameter, defaulting
   to today's resolution. Without it a hosted agent loads the machine's own
   `appsettings`/`config.json` **and** the user secrets that
   `BackerAgent/Program.cs:111-135` layers in — so a test run would pick up
   the real OneDrive and Dropbox client secrets of whoever ran it.
7. `RCloneServiceOptions.ConfigDirectory`: where `backer-rclone.conf` lives,
   defaulting to the machine's Backer config directory. **Found the hard way**
   — see Gate 2's result note. `RCloneService` rewrites that file on every
   `StartAsync` (`RCloneService.cs:2196-2199`) and after every backend login
   (`:1201`), with no way to redirect it.

### Wiring the agent to the in-memory Api (L3/L4)

- The agent's Hannibal client is registered via `AddHttpClient<...>` with a
  base address from options (`application/Hannibal/Client/DependencyInjection.cs:27-32`),
  so the harness calls `ConfigurePrimaryHttpMessageHandler(() =>
  apiFactory.Server.CreateHandler())`.
- SignalR `HubConnection`s are constructed in `BackerAgent/Program.cs` and
  injected as a `Dictionary<string, HubConnection>` consumed by
  `HubConnectionService` (`application/Hannibal/Client/HubConnectionService.cs:6-12`).
  The harness overrides that dictionary with connections built over the
  `TestServer`. **Use `HttpTransportType.LongPolling`** and
  `HttpMessageHandlerFactory`; that works over `TestServer` without the
  WebSocket plumbing, and the transport is not what is under test.
- The agent authenticates with `RCloneService:BackerUsername/BackerPassword`;
  the harness registers that user through the same endpoint the existing
  `BackerApiFactory.RegisterAndGetTokenAsync` uses.

### Determinism rules

Non-negotiable, because a flaky end-to-end suite gets muted and then deleted:

- No test asserts on wall-clock duration. Readiness is asserted by polling a
  condition with a generous ceiling, or by the stub's recorded call log.
- No `Task.Delay` as a synchronisation primitive in test code. The stub and
  the harness expose awaitable signals ("the agent has called `sync/copy`",
  "the job has reached state X").
- Every test seeds its own users, storages, endpoints and rules with unique
  names; `ApiIntegrationTestBase.ArrangeAsync` already resets the DB between
  tests and that contract is kept.
- The 120-second server-side job timeout
  (`application/Hannibal/Services/HannibalServiceJobs.cs:205-222`) is real in
  these tests. Anything needing to cross it uses the stub's clock control, not
  a two-minute sleep.

## Gates

Each gate is independently verifiable and independently committable. A gate is
**met** only when every criterion is demonstrably true from command output.
Baseline for every gate: `dotnet build Backer.sln` clean, and the suite still
green. Measured on `3d9d354` before any of this work: `Hannibal.Tests` 37,
`WorkerRClone.Tests` 31 passed + 1 skipped, `Tools.Tests` 9 — **77 passed, 1
skipped, 78 total** — plus `Hannibal.IntegrationTests` **26**, which need a
local PostgreSQL and skip cleanly without one.

### Gate 1 — Stub rclone — **MET (2026-08-20)**

New `tests/TestSupport.RClone/` (a library, so both L4 and L5 can use it):
scriptable in-process rclone RC stub per §"The stub rclone". Contract tests
live in `tests/WorkerRClone.Tests/Client/RCloneStubContractTests.cs` rather
than a new test project, because that project already references
`WorkerRClone`.

**Acceptance**
1. The stub answers all eleven paths `RCloneClient` calls, and rejects
   requests without the `who:how` basic auth. ✔
2. Unit tests drive the **real** `RCloneClient`
   (`worker/WorkerRClone/Client/RCloneClient.cs`) against the stub and get
   correctly deserialised results for every one of those calls. The stub does
   not reference `WorkerRClone` and writes its JSON by hand, so this is a
   two-sided check rather than a type round-tripping to itself. ✔
3. Scripted behaviours demonstrated: succeed-after-N-polls, fail-with-error,
   stall, stop-mid-job, unknown-job error, and custom `core/stats`
   (including `transferring` entries). **Stderr scripting is not part of this
   gate** — see §"The stub rclone"; it needs the process seam and moves to
   Gate 2. ✔ (as amended)
4. The stub records every request with its parsed body, and a test asserts on
   a recorded `sync/copy` source/destination pair. ✔
5. The stub binds an ephemeral port; two instances run concurrently in one
   test run without collision. ✔
6. `WaitForRequestAsync` makes "the agent got there" awaitable, counts
   requests that already arrived, and fails with a message naming the paths
   actually seen — so no test in later gates needs a sleep. ✔

**Result.** `dotnet test tests/WorkerRClone.Tests/` — 53 passed, 1 skipped
(the pre-existing opt-in live OneDrive check), 54 total; up from 31 passed /
32 total. 22 new tests. No new build warnings (the warnings emitted by
`worker/WorkerRClone` predate this work).

### Gate 2 — The agent is hostable in a test — **MET (2026-08-20; AC6 closed 2026-08-21)**

Seams 1–3, 6 and 7 from §"Production seams required", plus
`tests/BackerAgent.IntegrationTests/` with an `AgentHostFactory` that starts
`BackerAgent` in-memory pointed at a Gate 1 stub and a substitute Hannibal
client.

Hosting uses `WebApplicationFactory<BackerAgentHost>`, not
`WebApplicationFactory<Program>`: `Api/Program.cs:821` already exports a public
`Program` in the global namespace, and a second one would leave the Gate 3
project — which references both — unable to name either without an extern
alias. `BackerAgent` therefore exports a purpose-named marker type instead.

**Acceptance**
1. `BackerAgent` starts under `WebApplicationFactory` and reaches its
   `Running` state against the stub, with **no rclone process spawned** —
   asserted by process enumeration before and after. ✔
2. With `RCloneUrl` unset the resolution still yields `http://localhost:5572`,
   and a blank or whitespace value is ignored rather than used. ✔ —
   asserted on the pure `_resolveRCloneUrl`, deliberately *not* by hosting an
   agent against the default port: such a test could contact a real rclone on
   the developer's machine and rewrite its configuration.
3. The address rclone prints is turned into a usable absolute URL and
   remembered. ✔ for the conversion (`_toRCloneUrl`, including rejection of
   unusable input) — and note the original dead assignment
   (`RCloneService.cs:1017-1025`) was **doubly** broken: the captured group is
   `host:port` with no scheme, so even had it been kept it would have thrown
   when used as an `HttpClient` base address. ⚠ **Gap:** the regex that
   extracts the address is inline in `_startRCloneProcess` and still has no
   test; covering it needs a fake child process, which no gate currently owns.
4. `SkipProcessStart = true` prevents the spawn even when `RClonePath` points
   at a real executable — asserted on `_processRClone` remaining null. ✔
   A non-existent path would have proved nothing: a failed spawn leaves that
   field null exactly as a skipped one does.
5. The agent shuts down cleanly within the test's timeout; no orphan
   processes remain. ✔
6. Manual smoke, `dotnet run --project BackerAgent/` against a real rclone,
   confirming the `app.Run()` change did not affect service startup. ✔ Run by
   Timo 2026-08-21: with no rclone running beforehand, the agent started one
   (so the spawn path was exercised, not the attach path) and terminated it
   again on shutdown. Made safe to run beside the user's live agents by the
   `RCloneService:SkipJobAcquisition` option added for exactly this purpose —
   the agent logs in, connects and runs rclone but never acquires jobs.
7. **Stderr classification is testable.** `_readPrintLog`
   (`RCloneService.cs:911`) loops on `_processRClone.HasExited`, so it cannot
   be called without a process; the per-line handling is now
   `_handleStderrLine`. Covered: an `ERROR : ` line is collected without its
   prefix; the buffer caps at 200 and drops oldest first; `couldn't fetch
   token` and `maybe token expired` each increment `_stderrTokenErrorCount`;
   an ordinary error and a non-error line do not. ✔ This is the trigger for
   early re-authentication and had no test at all.
8. **The agent writes `backer-rclone.conf` into the test's own directory**,
   and the real one's last-write time is unchanged across the run. ✔

**Result.** `dotnet test tests/BackerAgent.IntegrationTests/` — 13 passed, 0
skipped, in 59 s (the two process-start tests each wait out the agent's ten
one-second probes). `tests/WorkerRClone.Tests` 63 passed / 1 skipped / 64.
Suite totals: **122 passed, 1 skipped, 123 total**, up from 77/1/78.

**Two findings worth acting on separately.**

- *The harness rewrote the real `backer-rclone.conf`.* Before seam 7 existed,
  every hosted agent loaded and saved the machine's actual rclone config, and
  two hosts in parallel raced on that one file — which is how it surfaced. The
  file survived (`StartAsync` does load-then-save, so the four existing remotes
  round-tripped intact and were verified afterwards), but this is exactly the
  class of accident the standing "no test touches live data" constraint exists
  to prevent. Seam 7 closes it and Gate 2 AC8 keeps it closed.
- *`builder.Host.UseSerilog()` (`BackerAgent/Program.cs:59`) replaces the
  logger factory.* Its parameterless form discards every other logging
  provider, so no test can capture the agent's logs — assertions here use
  state and recorded stub requests instead. The same wiring means the
  Windows `AddEventLog` provider registered at `BackerAgent/Program.cs:26-31`
  receives nothing in production either. Not fixed here: changing logging
  wiring is a production behaviour change that deserves its own decision.

### Gate 3 — Full-loop happy path — **MET (2026-08-20)**

Seam 4 (`BackerApiFactory` modes), plus `tests/Backer.E2ETests/` hosting the
Api factory and the agent factory in one process, joined by `TestServer`
handlers per §"Wiring the agent".

Both harnesses were first extracted into libraries so two suites can share
them: `tests/TestSupport.Api/` (`PostgresFixture`, `BackerApiFactory`, the
recording hub, the OAuth2 transport stub) and `tests/TestSupport.Agent/`
(`AgentHostFactory`). Each test assembly keeps its own `PostgresCollection`,
because xUnit only discovers a `[CollectionDefinition]` alongside the tests
that use it.

**Acceptance**
1. A test creates a user, two storages, two endpoints and a rule over REST and
   observes: a `Job` row appears, the agent acquires it, the stub receives
   `sync/copy` with `e2esource:/Documents/Work` → `e2edest:/Backups/Daily`,
   the agent reports completion, and the `Job` reaches `DoneSuccess` in
   PostgreSQL. ✔
2. ⚠ **Amended — the premise was wrong.** The agent does not configure rclone
   through the RC API at all: it writes `backer-rclone.conf` itself
   (`_configManager.AddOrUpdateRemote` then `SaveToFile`,
   `RCloneService.cs:691`), and `RCloneClient.CreateConfigAsync` has **no
   caller anywhere in the codebase**. The test therefore asserts on the file —
   a `[e2esource]` section carrying `type = local` — and additionally asserts
   that no `config/create` request ever reaches the stub, so the day that
   changes, this fails. ✔ as amended.
3. The existing 26 API integration tests pass unchanged, proving both the
   extraction and the mode defaults preserved today's behaviour. ✔
4. `HubMode.Real`: the agent opens a genuine SignalR connection to the real
   hub (`Api/Program.cs:167`) and reaches `Connected` — the first test in the
   codebase to exercise SignalR rather than a recording fake. ✔
5. Three consecutive green runs, 17 s each. ✔

**Result.** `dotnet test tests/Backer.E2ETests/` — 4 passed, 17 s. Suite
totals: **152 passed, 1 skipped, 153 total.**

**Four findings.**

- *A reported failure requeues the job rather than recording it.*
  `ReportJobAsync` turns a reported `DoneFailure` into `Ready` with
  `Owner = ""` (`HannibalServiceJobs.cs:361-372`), so the work is retried and
  no failed state is ever persisted. Nothing counts the attempts — the
  `TXWTODO` at `:367` says as much — so a job that always fails is retried
  indefinitely. The test now asserts the real behaviour: a second `sync/copy`
  arrives, and the retry succeeds. A retry cap is worth its own decision.
- *`Owner` is cleared on success too* (`:376-377`), so a completed job does not
  record which agent ran it.
- *The agent's login credentials bypass the options pipeline.*
  `BackerAgent/DependencyInjection.cs:44-45` binds a **fresh**
  `RCloneServiceOptions` straight from `IConfiguration` inside the
  `AutoAuthHandler` token callback, instead of using the registered
  `IOptionsMonitor<RCloneServiceOptions>` the rest of the agent uses.
  Anything that adjusts those options through the options pipeline — including
  a runtime change — is invisible to authentication. The harness works around
  it by writing the same values into configuration as well; production would
  be better served by reading the options it already has.
- *SignalR is load bearing for latency, not just for notifications.* With the
  hub connection dropped, the agent only notices requeued work on its
  120-second safety-net poll (`_jobPollInterval`, `RCloneService.cs:64`).
  Wiring it took the full-loop suite from 77 s to 17 s.

### Gate 4 — Scheduler determinism

Seam 5 (`TimeProvider` in `RuleScheduler`), and the scheduler enabled in
full-loop tests.

**Acceptance**
1. `RuleScheduler` takes `TimeProvider`; all ten `DateTime.UtcNow` reads
   (`RuleScheduler.cs:132, 218, 338, 341, 412, 472, 475, 487, 510, 529`) go
   through it. Production registers the system provider; nothing changes at
   runtime.
2. With a fake time provider: a rule whose last job completed
   `MaxDestinationAge` ago produces exactly one new job when time advances —
   and none before, per `ScheduleCalculator.cs:46-54`.
3. After a `DoneFailure`, no job is created before
   `LastReported + MinRetryTime` and exactly one after
   (`ScheduleCalculator.cs:56-62`).
4. A rule created *after* the scheduler started is picked up (event path,
   `RuleScheduler.PublishEventAsync:84`), not only at
   `InitializeScheduleAsync`.
5. No duplicate jobs: a rule processed twice in one pass yields one job row.
6. Anti-starvation and dependency deferral paths
   (`RuleScheduler.cs:306-341`) each have one test.

**Result (2026-08-21) — MET, one finding.** `RuleScheduler` had grown to
**seventeen** `DateTime.UtcNow` sites (ten at planning, five more from the
dependency work, two more since); all are behind an injected `TimeProvider`
now, `TryAddSingleton(TimeProvider.System)` at the single production wiring
point, and a grep proves zero raw reads remain. The deterministic tests
(`SchedulerDeterminismTests`, 7) construct the real scheduler directly
against the fixture database with a `FakeTimeProvider`, drive passes via
`PublishEventAsync` (the wakeup semaphore, deliberately left on real time,
answers immediately), and assert both sides of every boundary — including
that Gate E's *terminal* failures schedule identically through
`MinRetryTime`. **Finding (pre-existing, pinned not fixed):** the
anti-starvation escalation is unreachable — `ProcessReadyRulesAsync`
removes the rule from `_scheduledRules` before `AreDependenciesSatisfied`
(its only caller path) looks the entry up, so `DependencyDeferredSince` is
never stamped and the starvation escape never fires, no matter how long a
rule is deferred. This corroborates `docs/plan-job-dependency-ordering.md`'s
"layer 1 is not a dependency mechanism" diagnosis and belongs to that
plan's fix, not this gate. Suite: **296 passed, 1 skipped**.

### Gate 5 — Failure and re-auth paths

The paths `docs/plan-onedrive-oauth2-reauth.md` and `docs/plan-phase2-gates.md`
were about, now testable end to end.

**Acceptance**
1. Stub fails a job ⇒ `Job` reaches `DoneFailure` and the rule reschedules per
   `MinRetryTime`.
2. Stub emits three token-error stderr lines ⇒ the agent triggers the early
   token-refresh path (`_stderrTokenErrorThreshold`, `RCloneService.cs:60`),
   asserted by the resulting refresh attempt, not by log scraping.
3. Stub stalls ⇒ the OAuth2 inactivity timeout (`RCloneService.cs:67`) fires
   and the mid-job refresh path runs.
4. An expired-token storage causes `_ensureConfiguredEndpoint` to throw
   `UnauthorizedAccessException` (`RCloneService.cs:686`) and the job fails
   with a message naming the storage — rather than hanging.
5. Agent reports nothing for longer than 120 s (fake clock) ⇒ the server times
   the job out (`HannibalServiceJobs.cs:205-222`) and it becomes
   re-acquirable. This is the contract the git engine's heartbeat depends on,
   so it must be pinned by a test before that engine exists.
   **AC5 done early (2026-08-21)**, pulled forward per the git plan's
   execution order: `JobTimeoutContractTests` (2 tests, no fake clock —
   stale `LastReported` seeded directly). Pinned precisely: a timed-out job
   is retired to **`DoneFailure` permanently** — "re-acquirable" is the
   *endpoint slot*, which the next Ready job takes; the job row itself never
   returns to Ready. A fresh `LastReported` keeps the job alive and its
   endpoints blocked, which is the half the heartbeat relies on. The rest of
   Gate 5 remains open.
6. Stub restarts mid-job ⇒ the agent's state machine recovers rather than
   wedging.
7. A full OAuth2 re-auth round trip: trigger, callback, storage updated,
   `StorageReauthenticated` broadcast received by a real SignalR client, agent
   reloads storages (`RCloneService.cs:1535`) and the next job succeeds.

### Gate 6 — Concurrency and safety

**Acceptance**
1. Two agents, one ready job: exactly one acquires it; the other gets the
   not-found path and the job is not double-owned.
2. `PathsOverlap` in practice: a job writing `endpoint/a` blocks a job writing
   `endpoint/a/b` (`HannibalServiceJobs.cs:289-305`, `:319`), and two readers
   of one source proceed concurrently.
3. `Networks` filtering: an agent whose network does not match a storage's
   never receives that job (`HannibalServiceJobs.cs:144-158`).
4. User isolation: user A's agent never receives user B's job — extending
   `HostIsolationTests` to the full loop.
5. Capability filtering, once `docs/plan-git-repo-storage.md` Gate C lands,
   plugs into this same suite. Stated here so the seam is designed for it now.

**Result (2026-08-21) — MET**, landed alongside git-plan Gate C in
`tests/Hannibal.IntegrationTests/ConcurrencySafetyTests.cs`, and the gate
earned its keep before it was even done: writing the tests exposed **two real
acquisition defects**, both fixed in the same PR.

- *No user isolation (AC4).* `AcquireNextJobAsync`'s candidate query had no
  user filter and `AcquireParams.Username` was never read — any authenticated
  agent could acquire any user's job. Fixed: `j.UserId == _currentUser.Id` in
  the candidate query (the Job.UserId creation-path audit found every minted
  job sets it from its rule).
- *Double-grant race (AC1).* The read-then-write claim let two concurrent
  acquires both win one job — reproduced 27/27 on a cold host, both callers
  receiving 200 with different Owners. Fixed: the claim is a single
  conditional `ExecuteUpdateAsync` (`UPDATE … WHERE State=Ready AND
  Owner=''`); the loser matches zero rows and moves to the next candidate.
  The claim-in-loop shape also resolved the old `TXWTODO` — the earliest-
  `StartFrom` eligible candidate now wins, where previously the *last* one
  did because the loop kept overwriting its pick.
- AC1 runs both sequentially and as a genuinely concurrent `Task.WhenAll`
  test (2 and 4 callers, exactly one grant, winner-agnostic). AC2a/AC2b pin
  writer-blocks-nested-writer / concurrent readers; AC3 the Networks pair;
  AC5 proves capability, network and user filters compose. A test-data
  finding: several earlier acquisition tests seeded jobs under placeholder
  UserIds and only passed because isolation was missing; their seeds now use
  the authenticated caller's real id. Integration suite stable across five
  consecutive runs (3 by the implementing agent, 2 in verification).

### Gate 7 — REST surface and config round-trip

The breadth coverage that is cheap once L1 exists.

**Acceptance**
1. CRUD round-trips over REST for storages, endpoints and rules, including
   the update path that `edf74ea` fixed (rule endpoint changes must survive an
   update) — a regression test for a bug that has already bitten once.
2. Export → wipe → import reproduces storages, endpoints and rules exactly.
3. `includePasswords=false` omits secrets from the export, and importing such
   a file leaves existing secrets intact rather than blanking them
   (`HannibalServicePorter.cs:170, 380, 408`).
4. Every REST endpoint in `Api/Program.cs` is either covered by a test or
   listed here as deliberately uncovered, with a reason. No silent gaps.

### Gate 8 — A launchable local stack

`contrib/test-stack/docker-compose.test.yml` (or an equivalent process
launcher) bringing up PostgreSQL, `Api`, `Poe` and `BackerAgent` wired to a
stub rclone, on ephemeral ports, with generated throwaway credentials.
Explicitly **not** the production `docker-compose.yml`.

**Acceptance**
1. One command brings the stack up; a health check on each service passes;
   one command tears it down leaving no volumes or containers.
2. Ports and database name are per-run unique, so two runs can overlap.
3. No credential in the file is a real one, and none is reused from
   `docker-compose.yml`.
4. The stack starts from a clean clone plus `git submodule update --init`
   (`external/OAuth2` is a submodule and is empty in a fresh worktree).

### Gate 9 — Browser journeys

`tests/Backer.BrowserTests/` using Microsoft.Playwright against the Gate 8
stack.

**Acceptance**
1. Journey: register → log in → create storage → create two endpoints →
   create rule → observe the job appear and reach success on the Landscape
   page. Asserted against the DB as well as the DOM, so a green page cannot
   mask a broken backend.
2. Journey: edit a rule's endpoints and confirm the change persists after
   reload (the `edf74ea` bug, this time through the UI that produced it).
3. Journey: export config, re-import into a clean database, confirm the UI
   shows the same landscape.
4. Failure surfacing: with the stub scripted to fail, the UI shows the job as
   failed rather than silently stalling.
5. Browsers install via `playwright install` in CI; the suite is opt-in behind
   an environment variable so `dotnet test` on a dev machine without browsers
   still passes.
6. Traces and screenshots are captured on failure and written to a known path.

### Gate 10 — Make it stick

**Acceptance**
1. `docs/TESTING.md` documents all five layers, what each covers, and how to
   run them.
2. A single command runs L0–L4; a second runs L5.
3. CI (or a documented local pre-merge command) runs L0–L4 on every change.
4. Total L0–L4 wall-clock recorded here, with a stated budget; if it exceeds
   the budget the suite gets split rather than muted.
5. Three consecutive full green runs recorded before the gate is declared met.
6. **The stub's fidelity to real rclone is closed out.** An opt-in test,
   guarded by an environment variable and skipping cleanly like the existing
   live OneDrive check, drives `RCloneClient` against a real rclone binary and
   asserts the same responses the stub produces — in particular which of
   `jobids` / `jobsids` / `running_ids` / `finished_ids` `job/list` really
   returns, since `RCloneService.cs:1390-1394` stops jobs based on
   `running_ids`. Until this runs, every stub payload is asserted, not
   verified.

## Sequencing against the git work

The git plan does **not** need all ten gates. It needs:

- **Gates 1–3** before `docs/plan-git-repo-storage.md` Gate D, so the git
  engine is built against a working full-loop harness rather than after it.
- **Gate 4** before that plan's Gate F (scheduler fit) — otherwise the
  scheduler ACs there are untestable.
- **Gate 5 AC5** (the 120-second timeout) before that plan's Gate D AC8
  (heartbeat), since the heartbeat exists solely to satisfy that contract.
- **Gate 6** alongside that plan's Gate C, which extends the same acquisition
  filter.

Gates 7–10 improve confidence broadly but do not block the git engine.
Recommended order: 1, 2, 3, 4, 6, 5, 7, 8, 9, 10 — failure paths (5) after
concurrency (6) because 5 is the largest and benefits from a settled harness.

## Verification

Per-project, because `dotnet test` with several project paths in one
invocation fails with `MSB1008` on SDK 9.0.308:

```bash
git submodule update --init --recursive   # external/OAuth2 is empty in a fresh worktree
dotnet build Backer.sln
dotnet test tests/Hannibal.Tests/
dotnet test tests/WorkerRClone.Tests/
dotnet test tests/Tools.Tests/
dotnet test tests/Hannibal.IntegrationTests/     # needs local PostgreSQL
dotnet test tests/BackerAgent.IntegrationTests/  # from Gate 2
dotnet test tests/Backer.E2ETests/               # from Gate 3
dotnet test tests/Backer.BrowserTests/           # from Gate 9, opt-in
```

Standing constraints, inherited from `docs/TESTING.md`:

- No test touches live backup data, starts rclone, or writes to a real cloud
  account. The stub rclone makes this structural rather than a convention.
- Any suite requiring PostgreSQL skips cleanly when it is unavailable, as
  `PostgresFixture` already does.
- No test asserts on wall-clock duration, and no `Task.Delay` is used as a
  synchronisation primitive.
