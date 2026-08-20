# Git repositories as backup source and target

Status: planned, not yet executed.
Revision 2 — rewritten after adversarial review (see §"Review history").

## Context

Backer today moves *files* between object/file stores through rclone. A git
repository is not a file store: its content is a ref graph plus packfiles
negotiated over the smart-HTTP protocol. Backing up repositories — mirroring
GitHub to Codeberg, or GitHub to a bare repo on the NAS — is therefore not a
new rclone remote, it is a **second transfer engine** alongside rclone.

The user-facing model stays the one Backer already has:

| Backer concept | Git meaning |
|---|---|
| `Storage` | a git *host account*: base URL + one credential (PAT) |
| `Endpoint` | one repository on that account, `Path` = `owner/repo` |
| `Rule` | "mirror this repo to that repo, at most this stale" |
| `Job` | one mirror run |

### Scope decided with Timo (2026-08-20)

1. **Fidelity: full mirror.** All branches, tags and notes, full history. The
   destination is itself a clonable repository. Forge metadata (issues, PRs,
   releases, wiki) is explicitly *not* backed up in this plan.
2. **Direction: `git → git` only in phase 1.** GitHub → Codeberg, GitHub →
   local bare repo. `git → OneDrive/SMB` (bundle) and `storage → git`
   (restore-by-commit) are sketched in §"Later phases", not gated here.
3. **Credentials live on `Storage` only.** One `Storage` per (host, account).
   Two accounts on the same host are two Storages. No per-endpoint secret.

### Key facts (verified 2026-08-20 against `3d9d354`)

Transfer path:

- Every transfer is an rclone RC call: `RCloneClient.CopyAsync` /
  `SyncAsync`, `worker/WorkerRClone/Services/RCloneService.cs:734` and `:745`.
  There is no other execution path for a `Job`.
- The transfer address is an rclone remote reference,
  `$"{endpoint.Storage.UriSchema}:/{endpoint.Path}"`,
  `worker/WorkerRClone/Services/RCloneService.cs:700`. `Storage.UriSchema`
  *is* the rclone remote name: `worker/WorkerRClone/Services/RCloneService.cs:691`.
- The whole `IStorageProvider` contract is "produce an `rclone.conf` section":
  `Task<Dictionary<string,string>> BuildRCloneParametersAsync(...)`,
  `worker/WorkerRClone/Services/Providers/IStorageProvider.cs:79`. A git host
  has no such section, so **git cannot be modelled as an `IStorageProvider`**.
  Providers are registered one line each in
  `worker/WorkerRClone/DependencyInjection.cs:65-72`; the only hosted job
  consumer is `services.AddHostedService<RCloneService>()` at `:46`.
- **Jobs are not the only path from a Storage to rclone.conf.** At startup
  `_checkOnlineImpl` loads *every* storage of the user
  (`worker/WorkerRClone/Services/RCloneService.cs:1101-1102`, reloaded after
  reauth at `:1535`), and `_backendsLoginImpl` then walks that list, creates a
  storage state for each (`:1190`), writes a remote named by its `UriSchema`
  (`:1193`) and saves the file (`:1201`). An unsupported technology does not
  fail — `RCloneStorages` logs a warning and returns a state with *empty*
  parameters (`worker/WorkerRClone/Services/RCloneStorages.cs:68-74`), which
  is then cached and written to `rclone.conf` as an empty section. **A `git`
  storage added naively would silently appear in `rclone.conf` on every agent
  boot.** This is why Gate A must filter, not merely abstain.

Domain model:

- `Storage` already carries everything phase 1 needs: `Technology:7`,
  `UriSchema:8`, `Networks:9`, `Username:21`, `Password:22`, `Host:23` —
  `application/Hannibal/Models/Storage.cs`. No migration in Gate A.
- **Nothing validates `Technology`.** `Technologies.GetTechnologies()`
  (`application/Hannibal/Models/Technologies.cs:5-13`) only feeds the UI
  dropdown (`frontend/Poe/Components/Pages/BackerPages/Storages.razor:200`);
  `CreateStorageAsync` (`application/Hannibal/Services/HannibalServiceStorages.cs:32-43`)
  inserts whatever it is given. A `"git"` storage can be POSTed today.
- **Nothing validates rules either.** `CreateRuleAsync`
  (`application/Hannibal/Services/HannibalServiceRules.cs:11`) performs no
  checks; its `null ==` guards are dead code because `FirstAsync` throws
  first. Gate B creates rule validation, it does not extend it.
- `Endpoint` has an `internal` constructor deriving
  `Name = "{userId}:{Technology}:{Path}"`
  (`application/Hannibal/Models/Endpoint.cs:22`) — but it has **zero callers**
  (`grep "new Endpoint("` finds none). The API persists whatever `Name` the
  client posts (`application/Hannibal/Services/HannibalServiceEndpoints.cs:8-26`)
  and the UI treats it as free text
  (`frontend/Poe/Components/Pages/BackerPages/Endpoints.razor:140`). Endpoint
  names are **not** derived and **not** unique. Nothing comes for free here.
- `Rule.RuleOperation` is `Nop | Copy | Sync`,
  `application/Hannibal/Models/Rule.cs:27-32`; `Job.Operation` carries the same
  enum, `application/Hannibal/Models/Job.cs:43`. `Job.JobState.DoneWithErrors`
  is `application/Hannibal/Models/Job.cs:65`.
- Operation can be rewritten in bulk on **all** existing jobs at runtime:
  `PUT /api/hannibal/v1/jobs/operation` (`Api/Program.cs:502-512`), exposed in
  `frontend/Poe/Components/Pages/BackerPages/Rules.razor:384`. It is the
  global kill switch, and it can force `Nop` onto a git job.

Acquisition and liveness:

- `AcquireParams.Capabilities` exists (`application/Hannibal/Models/AcquireParams.cs:6`)
  but is only logged — `application/Hannibal/Services/HannibalServiceJobs.cs:126`
  (and named in the not-found message at `:190`). The agent hardcodes
  `Capabilities = "use_me"`, `worker/WorkerRClone/Services/RCloneService.cs:834`.
  **Any agent can acquire any job today.**
- The precedent for filtering at acquisition exists: `Networks` is matched
  between agent and both endpoints' storages,
  `application/Hannibal/Services/HannibalServiceJobs.cs:144-158`.
- Concurrency control is `_mayUseSourceEndpoint` (`:289-305`) and
  `_mayUseDestinationEndpoint` (`:319`) over `PathsOverlap`. Keys are
  storage-id-scoped (`_endpointKey`, `:90-95`), so **two Storage rows pointing
  at the same host and account defeat the check**. Concurrent *readers* of one
  source are deliberately allowed.
- **There is a 120-second liveness contract.** `_gatherEndpointAccess` runs on
  every acquire call and marks any `Executing` job whose `LastReported` is
  older than 120 s as timed out
  (`application/Hannibal/Services/HannibalServiceJobs.cs:205-222`).
  `LastReported` is refreshed only when the agent reports
  (`:357-358`). Meanwhile `RCloneService` polls acquire every 120 s
  (`_jobPollInterval`, `worker/WorkerRClone/Services/RCloneService.cs:64`,
  used at `:221`). A git engine that blocks on an initial `git fetch` for more
  than two minutes without reporting would be **timed out by its own sibling
  service's polling**, freeing the rule to mint a replacement job while the
  first push is still in flight.

Scheduling (the previous revision of this plan got this wrong):

- The live scheduler is `RuleScheduler`, registered at
  `application/Hannibal/Client/DependencyInjection.cs:44-54`.
  `BackofficeService` is **dead code** — its hosted registration is commented
  out at `application/Hannibal/Client/DependencyInjection.cs:40`.
- `ScheduleCalculator` uses exactly two rule fields: next run after success =
  `LastReported + MaxDestinationAge`
  (`application/Hannibal/Services/Scheduling/ScheduleCalculator.cs:46-54`), and
  after failure = `LastReported + MinRetryTime` (`:56-62`).
- **`Rule.MaxTimeAfterSourceModification` (`application/Hannibal/Models/Rule.cs:52`)
  is consumed by no scheduler at all** — only by rule CRUD
  (`HannibalServiceRules.cs:89,103`), export/import
  (`HannibalServicePorter.cs:114,217,541,562,579`) and the UI
  (`Rules.razor:522`). No design here may lean on it.

Agent host wiring (all of it is rclone-specific and must be paralleled):

- Progress callbacks are `RCloneService` properties
  (`worker/WorkerRClone/Services/RCloneService.cs:89-90`) wired in
  `BackerAgent/Program.cs:166` and `:172`.
- Abort is hardwired to rclone: `BackerAgent/Hubs/BackerControlHub.cs:74` and
  `BackerAgent/Program.cs:289`, both calling
  `RCloneService.AbortJobAsync` (`worker/WorkerRClone/Services/RCloneService.cs:1982`).
- Shutdown reports in-flight jobs in `StopAsync`
  (`worker/WorkerRClone/Services/RCloneService.cs:2043-2102`), invoked from
  `BackerAgent/Program.cs:229`.
- Export/import already round-trips `Host`/`Username`/`Password` with
  include-passwords and preserve-on-import semantics
  (`application/Hannibal/Services/HannibalServicePorter.cs:170,380,408`).
- UI switches on the technology string in three places:
  `frontend/Poe/Components/Pages/BackerPages/Storages.razor:200,342-384`,
  `.../Endpoints.razor:208-242`, `.../Landscape.razor:141-146` (which already
  has a default arm at `:147`).

Test baseline, measured 2026-08-20 on `3d9d354`:

| Project | Passed | Skipped | Total |
|---|---|---|---|
| `tests/Hannibal.Tests` | 37 | 0 | 37 |
| `tests/WorkerRClone.Tests` | 31 | 1 (live OneDrive, opt-in) | 32 |
| `tests/Tools.Tests` | 9 | 0 | 9 |
| **Sum** | **77** | **1** | **78** |

`tests/Hannibal.IntegrationTests` hosts `Api` under `WebApplicationFactory`
(`tests/Hannibal.IntegrationTests/BackerApiFactory.cs`) and needs a local
PostgreSQL. **There is no harness that runs `BackerAgent` in-process**; no
acceptance criterion below may assume one.

### Adjacent defect this work must route around (not fix)

`RCloneStorages` caches storage state keyed by **technology**, not by storage:
`_mapStorageStates[storage.Technology] = state`,
`worker/WorkerRClone/Services/RCloneStorages.cs:54` (lookup at `:48`). Two
Storages of the same technology — e.g. two OneDrive accounts — share one
cached state. Fixing it is out of scope; Gate A keeps git storages out of that
map by filtering them out of the agent's storage list entirely.

## Design

### 1. Technology: one generic `git`, forge flavours later

Phase 1 adds exactly one technology string: **`git`**.

- `Storage.Host` = remote base, e.g. `https://github.com/`,
  `https://codeberg.org/`, or a local root `D:/backup/git/`.
- `Storage.Username` / `Storage.Password` = user + personal access token.
  HTTPS only; SSH deferred (host-key trust and key storage are their own
  problem).
- `Storage.UriSchema` = short alias, used for logs and cache directory naming.
  It is **not** written to `rclone.conf`.
- `Endpoint.Path` = `owner/repo` (or a relative directory under a local root).
- Remote URL = `Host` + `Path` + (`.git` if not already present).

Forge-specific technologies (`github`, `gitea`) are deferred to the phase that
needs the forge REST API — auto-creating a missing destination repository,
expanding `owner/*` into endpoints, and setting the destination's default
branch. Until then, **the destination repository must already exist** (it may
be empty).

### 2. A second engine, not a second provider

New agent-side project `worker/WorkerGit/` with hosted service
`GitWorkerService`, sibling to `RCloneService`. It shares the Hannibal client,
job acquisition and reporting; it shares nothing with rclone.

The seam is at job acquisition: the server decides which engine a job needs,
and each agent service acquires only jobs for its own engine.

```
Hannibal ──Classify(job)──▶ engine = rclone | git
   │
   ├── acquire(caps="rclone") ─▶ RCloneService    (existing)
   └── acquire(caps="git")    ─▶ GitWorkerService (new)
```

`RCloneService`'s job logic is not restructured, but it is **not untouched
either** — three bounded, named changes are required and are gated:

1. `worker/WorkerRClone/Services/RCloneService.cs:834` — send `"rclone"`
   instead of `"use_me"` (Gate C).
2. `worker/WorkerRClone/Services/RCloneService.cs:1102` and `:1535` — filter
   the storage list through a shared predicate
   `Technologies.IsRCloneTechnology(technology)` so git storages never reach
   `RCloneStorages` or `rclone.conf` (Gate A).
3. Nothing else.

`BackerAgent` needs four additions, all listed in Gate D: DI registration of
`GitWorkerService`; abort routing by engine in `BackerControlHub`
(`BackerAgent/Hubs/BackerControlHub.cs:74`) and `Program.cs:289`; progress
callback wiring beside `Program.cs:172`; and shutdown reporting parity with
`RCloneService.StopAsync`.

Mixed pairs (git endpoint ↔ non-git endpoint) are **rejected at rule
creation** until the bundle phase lands. Silently accepting a rule that can
never produce a runnable job is the worse failure.

### 3. Ref namespace policy — never `refs/*`, never `--mirror`

This is the single most important mechanical decision, and the obvious
implementation is wrong.

`git ls-remote` / `git fetch +refs/*:refs/*` against GitHub returns
`refs/pull/*/head` and `refs/pull/*/merge` — frequently thousands of refs on a
real repository. `git push --mirror` then attempts to push them, and the
destination forge **refuses** writes to its own reserved namespace ("deny
updating a hidden ref"). Every mirror run toward a forge destination would end
in errors, permanently. (Codeberg/Forgejo reserves `refs/pull/*` similarly;
confirm during Gate H.)

Therefore the engine works with an **explicit, closed set of namespaces** on
both legs:

```
MIRRORED = refs/heads/*  refs/tags/*  refs/notes/*
```

- fetch: `git fetch --prune --prune-tags <src> <refspec per namespace>`
- push (Copy): same refspecs, **not forced**
- push (Sync): same refspecs, forced, plus explicit deletions computed as
  (destination refs in MIRRORED) − (source refs in MIRRORED)

Deletion is thus always a computed, bounded list the engine can log and guard,
never an implicit consequence of `--mirror`. Anything outside MIRRORED on the
destination is left alone — including the forge's own hidden refs and the
marker ref below.

### 4. The destination is the baseline

The previous revision stored a "last known ref fingerprint" on the agent. That
was wrong in three ways: it does not survive cache deletion, each agent holds
a private copy, and — worst — if it is recorded at fetch time then a run that
fetches successfully but fails to push will thereafter *skip* while reporting
`DoneSuccess`, i.e. a silent backup outage that looks green forever.

Instead, **both sides are probed and compared directly**:

```
1. srcRefs  = git ls-remote <source>      refs/heads/* refs/tags/* refs/notes/*
2. dstRefs  = git ls-remote <destination> refs/heads/* refs/tags/* refs/notes/*
3. if srcRefs == dstRefs  → DoneSuccess, "already in sync", no transfer
4. guards (§5), computed from srcRefs vs dstRefs
5. fetch into cache, push, then re-probe the destination to confirm
```

Two `ls-remote` calls cost milliseconds and no persistent state at all. The
skip decision is by construction keyed on (source, destination), survives
agent restarts, cache loss and multiple agents, and **cannot report success
while the destination is stale**. The shrink guard's baseline is the
destination's own ref list, which is exactly the thing being protected.

Notes are included in the probe; a notes-only change therefore invalidates the
comparison and propagates, matching the promised fidelity.

### 5. Safety policy — the part that matters

A mirror is a *destructive* operation on the destination. If the source
account is compromised, emptied, or merely misconfigured, an unguarded mirror
faithfully destroys the backup. All guards run after step 2 of §4, before any
push:

- **Zero-ref guard.** Source resolves to zero refs in MIRRORED → fail, never
  push.
- **Shrink guard.** Refs present on the destination but absent from the source
  exceed `Git:MaxRefShrinkPercent` (default 50) of the destination's ref count
  → fail, naming the refs. Per-rule override.
- **Force-push guard.** This is the attack the previous revision missed
  entirely: a compromised source that force-pushes garbage over every branch
  leaves the ref *count* identical, so a shrink guard alone passes and `Sync`
  destroys the backup's value. The engine therefore classifies each ref whose
  SHA changes as fast-forward or not (`git merge-base --is-ancestor` against
  the fetched objects) and fails when non-fast-forward updates exceed
  `Git:MaxNonFfPercent` (default 25). Per-rule override.
- **`Copy` never forces and never deletes.** Refspecs carry no leading `+`
  and no deletions. Refs rejected as non-fast-forward are reported and the job
  ends `DoneWithErrors` (`application/Hannibal/Models/Job.cs:65`). This is what
  makes Gate D safe to ship before the guards exist.
- **Adopt guard.** The destination carries a marker ref whose *name* encodes
  the source identity:
  `refs/backer/mirror-of/<sha256(normalised source URL)>`. Identity in the
  name, not in object content, so verification is a plain `ls-remote` lookup
  and no synthetic object content has to be parsed. The ref points at an empty
  commit created once in the cache. The marker lives **outside MIRRORED**, so
  §3's push can never delete it and §3's fetch can never import a spoofed one
  from a hostile source — the failure mode that made the previous revision's
  destination-side marker self-destruct on every run.
  First push to a destination that is non-empty and carries no matching marker
  fails; per-rule `AllowAdopt` overrides, and the marker is written **before**
  the first data push, so a crash mid-run cannot orphan it.
- **Self-mirror guard.** Source and destination normalising to the same URL is
  rejected at rule creation and re-checked at run time — necessary because the
  endpoint-in-use check is storage-id-scoped and blind to two Storage rows
  pointing at the same account (`HannibalServiceJobs.cs:90-95`).
- **A tripped guard is a security signal**, not a routine failure: it is
  logged at warning level with a distinct event id so it can be alerted on.

`Sync` is not enabled until Gate E ships all of the above.

### 6. Running git safely from a Windows service

- **Minimum git 2.31** (for `GIT_CONFIG_COUNT`/`GIT_CONFIG_KEY_n`), probed at
  startup with `git --version`. The agent advertises the `git` capability only
  if the probe succeeds, so an agent without git never receives git jobs.
- **Credentials never touch disk and never appear in argv.** `-c
  http.extraHeader=…` is an argv item — visible in any same-user process
  listing, and re-sent across cross-host redirects. Use `GIT_ASKPASS` pointing
  at a tiny helper that echoes the token from an inherited environment
  variable; git's native credential flow is host-scoped. Remote URLs stored in
  the cache carry no credentials.
- **`GIT_TERMINAL_PROMPT=0`** — otherwise a 401 makes git block forever
  waiting for a prompt that a service can never answer.
- **`GIT_CONFIG_NOSYSTEM=1`, an isolated `HOME`, and `credential.helper=`
  cleared** — otherwise Git Credential Manager on the machine can silently
  authenticate as the *logged-in developer* instead of the Storage's PAT,
  pushing to the wrong account with the wrong identity.
- **Watchdog.** Every child process gets an overall timeout
  (`Git:JobTimeout`, default 2 h) and a no-progress timeout
  (`Git:StallTimeout`, default 10 min). A stalled network is not a
  cancellation and must not look like one.
- **Heartbeat.** While a child process runs, the service reports the job as
  `Executing` every ≤30 s. Non-negotiable: the server times jobs out at 120 s
  (`HannibalServiceJobs.cs:205-222`) and a timed-out job is re-minted, which
  would put two agents on one destination.
- **`Nop`** — the bulk operation switch (`Api/Program.cs:502-512`) can force
  `Nop` onto a git job. The engine completes it as `DoneSuccess` without
  contacting either remote. It must not fall into a silent default the way
  `RCloneService.cs:738-740` fabricates `jobid = 0`.

### 7. Cache

Bare cache at `{CacheRoot}/{userId}/{sourceUriSchema}/{sanitised-path}.git`.

- **Locked.** Concurrent readers of one source are allowed by the server
  (`HannibalServiceJobs.cs:289-305`), so two rules mirroring one source to two
  destinations can run at once on one agent. A per-cache-directory lock file
  serialises them; without it, one job's delete-and-reclone recovery would
  `rm -rf` a directory another job is fetching into.
- **Disposable, but not blindly.** On corruption the cache is deleted and
  re-cloned once. Before doing so the engine checks free disk space — a
  disk-full mid-fetch looks exactly like corruption, and re-cloning needs
  *more* space than the fetch that just failed. Disk-full is reported as
  disk-full.
- **Maintained.** `git gc --auto` after each fetch; force-push churn otherwise
  accumulates unreachable objects forever. `Git:CacheQuotaBytes` with
  least-recently-used eviction of whole cache repos.
- `core.longpaths=true` on every cache repo; deep pack paths under a long
  cache root exceed `MAX_PATH` on Windows.

### 8. Known limitations, stated up front

- **Default branch is not propagated.** Plain git offers no way to set a
  remote's `HEAD` symref; `git remote set-head` is local only. A restored
  clone may check out the wrong default branch. Fixed in the forge-API phase;
  until then the restore note says so.
- **Large initial mirrors may not fit in one push.** GitHub rejects packs
  above roughly 2 GB. The engine pushes in ref batches and, if a single ref's
  history still exceeds the limit, fails with a message saying so rather than
  retrying forever.
- **Push is not atomic across refs** unless `--atomic` is supported by the
  destination; the engine requests it and tolerates its absence, in which case
  a partial push leaves a destination that §4's re-probe will detect as
  out-of-sync and the next run will complete.
- **git-LFS objects are not transferred.** Detected by reading
  `.gitattributes` at every MIRRORED branch tip via `git cat-file` in the bare
  cache and looking for `filter=lfs`; reported as `DoneWithErrors`.

### 9. Restore

Nothing to build: the destination *is* a repository, so restore is
`git clone <destination-url>` plus, per §8, an explicit
`git checkout <branch>` if the default branch matters. Gate D's integration
test exercises exactly this — it is what makes the backup meaningful.

### Explicitly out of scope

Issues, pull requests, releases, wiki, CI configuration and secrets;
submodule *content* (a mirror preserves gitlinks — the submodule is its own
endpoint); git-LFS object transfer; SSH authentication; wildcard/org
discovery; auto-creating destination repos; `storage → git` restore-by-commit;
the `RCloneStorages` technology-keying defect; the endpoint-in-use blindness
to duplicate Storage rows (mitigated here only by the self-mirror guard).

---

## Gates

Each gate is independently verifiable and independently committable. A gate is
**met** only when every acceptance criterion under it is demonstrably true from
command output, not from assertion.

**Ordering safety.** Gate A makes `git` storages creatable, and nothing today
validates rules — so a user could build a git rule the moment Gate A ships,
`RuleScheduler` would mint jobs from it, and every field agent (sending
`"use_me"`) would acquire and fail them on a retry loop forever. Gate B
therefore rejects **all** rules touching a git endpoint, and Gate D flips
git+git from rejected to allowed as its last step. Between A and D the feature
is inert by construction.

Baseline for every gate: `dotnet build Backer.sln` clean, and the 78-test unit
baseline in §Context still green.

### Gate A — `git` storages exist, and stay out of rclone

- Add `"git"` to `Technologies` and add
  `Technologies.IsRCloneTechnology(string)`.
- Filter the agent's storage list through it at
  `worker/WorkerRClone/Services/RCloneService.cs:1102` and `:1535`.
- Add server-side storage validation (there is none today): `Technology` must
  be in the known list; for `git`, `Host` non-empty and a valid absolute URL
  or filesystem root, `UriSchema` matching `^[a-z0-9][a-z0-9_-]*$` and unique
  per user. Add endpoint validation: `Path` matching
  `^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$` for URL hosts, no `..` segments.

**Acceptance**
1. Unit tests: `IsRCloneTechnology` true for the six existing technologies,
   false for `"git"`.
2. Integration test: a `git` Storage and two Endpoints under it round-trip
   through the API with all fields intact.
3. Integration test: each invalid input is rejected with a message naming the
   field — unknown technology, empty `Host`, colliding `UriSchema`, `Path`
   without exactly one `/`, `Path` containing `..`. (Note this is the first
   storage validation in the codebase; assert the six existing technologies
   still create successfully.)
4. `dotnet ef migrations list` is unchanged — Gate A adds no columns.
5. **The rclone.conf leak is closed**: a unit test over the filtered list
   proves a `git` storage is absent from what `_backendsLoginImpl` iterates.
   Because there is no in-process agent harness, this is asserted on the
   filter predicate and the list-construction sites, plus a manual smoke run
   of `BackerAgent` against a config containing one git storage, diffing
   `rclone.conf` before and after — result recorded in this document.

### Gate B — the server knows which engine a job needs, and blocks git rules

Pure function `JobEngine Classify(Endpoint source, Endpoint destination)` →
`Rclone` | `Git` | `Unsupported`, wired into rule create/update
(`application/Hannibal/Services/HannibalServiceRules.cs:11`, which validates
nothing today).

**Acceptance**
1. Unit tests: two non-git → `Rclone`; git+git → `Git`; git+onedrive and
   onedrive+git → `Unsupported`; unknown technology → `Unsupported`.
2. Rule creation with a mixed pair returns 4xx naming both technologies.
3. **Rule creation with a git+git pair is also rejected at this gate**, with a
   message saying the git engine is not yet available. Gate D removes this.
4. A rule whose source and destination normalise to the same git URL is
   rejected — including when the endpoints belong to two different Storages
   pointing at the same host and account.
5. All existing rule tests pass unchanged; creating and updating non-git rules
   is unaffected.

### Gate C — capability-aware acquisition

`AcquireParams.Capabilities` becomes a comma-separated engine set; the server
filters candidates in `AcquireNextJobAsync`
(`application/Hannibal/Services/HannibalServiceJobs.cs:142-173`) by
`Classify(job) ∈ caps`, alongside the existing `Networks` check.
`RCloneService` sends `"rclone"` (`worker/WorkerRClone/Services/RCloneService.cs:834`).

**Acceptance**
1. Integration test (`BackerApiFactory`): with one ready rclone job and one
   ready git job queued, an agent advertising `rclone` receives only the
   rclone job and one advertising `git` only the git job — independent of
   `StartFrom` ordering.
2. **Backwards compatibility**: an agent sending the legacy `"use_me"`, an
   empty string, or null is treated as `rclone`-only. Old agents are in the
   field; this is asserted, not assumed.
3. An agent advertising `rclone,git` can receive both.
4. When nothing matches, the existing not-found path is taken and the job
   stays `Ready` with `Owner == ""` — a mismatched agent must never park a job
   it cannot run.
5. Existing `Networks` filtering tests still pass.

### Gate D — mirror engine, additive push only

New `worker/WorkerGit/` and `tests/WorkerGit.Tests/`: `GitWorkerService`, git
CLI wrapper, cache manager with locking, credential injection, progress
parsing, heartbeat, watchdog. Plus the four `BackerAgent` integrations named
in §2. **Only `Copy` semantics** — §5 makes deletion and forcing structurally
impossible at this gate. Ends by allowing git+git rules (Gate B AC3 inverted).

**Acceptance**
1. Offline integration test, no network and no account: two bare repos in temp
   directories, source seeded with 2 branches, 1 lightweight tag, 1 annotated
   tag and 1 note. After the job the destination contains exactly those refs
   at the same SHAs — and nothing in `refs/pull/*` style namespaces is
   attempted (assert on the generated refspecs).
2. Restore test: `git clone` of the destination yields a working tree whose
   file content and `git log --all --format=%H` match the source.
3. Already-in-sync fast path: a second run performs two `ls-remote` calls, no
   fetch and no push. Asserted on the command trace, **not** on wall-clock
   time.
4. Stale-destination protection: delete a branch on the *destination* only;
   the next run detects the difference and restores it (this is the test the
   old fingerprint design would have failed).
5. Cache recovery: truncate a pack in the cache, rerun — the engine deletes,
   re-clones and succeeds, and says so in the log. With free disk below the
   required threshold it reports disk-full instead of re-cloning.
6. Cache locking: two jobs sharing one source cache run concurrently without
   error; assert the lock serialised them.
7. **No secret leakage**: after a run against a token-authenticated local HTTP
   stub, the cache tree, every `.git/config`, the captured process command
   lines, and the test's captured log output contain zero occurrences of the
   token. Explicit greps in the test, not eyeballing.
8. **Heartbeat**: a job whose child process is held for 150 s (a stub `git`
   that sleeps) reports `Executing` at least four times and is *not* timed out
   by the server. Run against `BackerApiFactory` so the real 120 s rule
   applies.
9. Watchdog: a child process that stalls past `Git:StallTimeout` is killed,
   the job reports `DoneFailure` with a stall message, and no orphan process
   remains.
10. `git --version` failing at startup ⇒ the agent does not advertise `git`,
    logs one clear warning, and acquires no git job.
11. `Nop` on a git job completes `DoneSuccess` without contacting either
    remote.
12. LFS: a source with `filter=lfs` in `.gitattributes` at a branch tip
    completes `DoneWithErrors` naming LFS.
13. Abort: `BackerControlHub.AbortJob` for a git job kills the child process
    and leaves the destination ref-identical to its pre-run state; abort for
    an rclone job still reaches `RCloneService.AbortJobAsync`.
14. Shutdown: `StopAsync` on `GitWorkerService` reports in-flight git jobs
    back to Hannibal and kills child processes, matching
    `RCloneService.cs:2043-2102` behaviour.
15. Progress reaches `BackerAgent`: the new callback fires at least once with
    a non-empty phase description during a fetch.

### Gate E — safety policy and `Sync` (true mirror)

Implements §5 in full and unlocks `Sync`. **This gate carries phase 1's only
migration**: two `Rule` columns, `AllowAdopt` and `AllowUnsafeRefChange`
(covering both the shrink and non-fast-forward overrides), plus their Porter
DTO fields and UI checkboxes.

**Acceptance**
1. `Sync` deletes a destination branch after it is deleted at the source;
   `Copy` on the same fixture does not.
2. `Sync` leaves refs *outside* MIRRORED on the destination untouched —
   seed the destination with a ref in a foreign namespace and assert it
   survives.
3. Zero-ref guard: empty source ⇒ job fails and destination
   `git for-each-ref` output is byte-identical before and after.
4. Shrink guard: destination with 10 refs, source with 2 ⇒ fails naming the 8;
   succeeds with the per-rule override.
5. **Force-push guard**: source force-pushes unrelated history over every
   branch, ref count unchanged ⇒ `Sync` fails naming the non-fast-forward
   refs, destination unchanged; succeeds with the override.
6. Non-fast-forward under `Copy`: destination branch unchanged, job ends
   `DoneWithErrors`, message names the ref.
7. Adopt guard: push to a non-empty unmarked destination fails; with
   `AllowAdopt` it succeeds and writes
   `refs/backer/mirror-of/<sha256>`; re-pointing that destination at a
   different source fails again. The marker survives a subsequent `Sync`
   (the regression the previous design would have failed) and is written
   before the first data push.
8. Every guard failure leaves the job in a state that honours
   `Rule.MinRetryTime` per
   `application/Hannibal/Services/Scheduling/ScheduleCalculator.cs:56-62` — a
   tripped guard must not spin.
9. Each guard failure emits a distinct warning event id suitable for alerting.
10. Migration applies and rolls back cleanly; existing rules default to
    overrides off.

### Gate F — scheduler fit

Git rules behave like any other rule under the **actual** scheduler
(`RuleScheduler` + `ScheduleCalculator`; note `MaxTimeAfterSourceModification`
is inert for every technology and this plan does not change that).

**Acceptance**
1. A git rule with `MaxDestinationAge = 1h` produces its next job at
   `LastReported + 1h` per `ScheduleCalculator.cs:46-54`; when the source is
   unchanged the job completes `DoneSuccess` via the §4 fast path.
2. After a guard failure the next job appears no earlier than
   `LastReported + MinRetryTime` (`ScheduleCalculator.cs:56-62`).
3. `Landscape.razor` and `Rules.razor` render a git rule's state without
   errors. (The icon switch already has a default arm at `Landscape.razor:147`,
   so this is about the rule/job panels, not the icon.)

### Gate G — UI and configuration round-trip

`Storages.razor` git branch (host, username, token, alias),
`Endpoints.razor` placeholder/help for `owner/repo`, `Landscape.razor` icon,
Porter fields for the Gate E rule columns.

**Acceptance**
1. A git Storage + 2 Endpoints + 1 Rule created end-to-end through the web UI
   with no console errors, and the resulting job runs.
2. Export → wipe → import reproduces Storage, Endpoints and Rule exactly
   including the Gate E override flags. With `includePasswords=false` the
   token is absent from the export and import leaves an existing token intact
   rather than blanking it — the mechanism already exists
   (`HannibalServicePorter.cs:170,380,408`), so this asserts it covers the new
   fields.
3. The token is never rendered back into the DOM after save (view-source
   check on the edit form).

### Gate H — opt-in live smoke test

Following `docs/plan-phase2-gates.md` Gate F's convention: runs only when
environment variables supply real credentials, skips cleanly otherwise.

**Acceptance**
1. With `BACKER_TEST_GIT_SOURCE` / `BACKER_TEST_GIT_DEST` unset, the test
   skips with a clear message and the suite stays green.
2. With them set to a real GitHub repo and a real Codeberg repo — the GitHub
   one deliberately having at least one open pull request, so `refs/pull/*`
   exists — a full mirror completes with **no ref errors**, `ls-remote` over
   MIRRORED matches on both sides, and a second run transfers nothing.
3. Confirmed during this gate: whether Codeberg/Forgejo reserves
   `refs/pull/*` the way GitHub does, recorded here either way.
4. Result recorded in this document with date, repo sizes and durations.

---

## Later phases (sketched, not gated)

- **Forge technologies.** `github` / `gitea` layered on `git`, using the forge
  REST API to auto-create a missing destination repository, set its default
  branch (§8), and expand `owner/*` into endpoints. This is what turns "mirror
  3 repos" into "mirror my whole account".
- **`git → existing storage`.** `git bundle create --all` into a staging
  directory, then the existing rclone leg ships the bundle to
  OneDrive/SMB/local. Naturally a two-job chain — see the job-dependency
  ordering work (currently an uncommitted draft in the main working tree, not
  a document this plan can rely on).
- **git-LFS.** `git lfs fetch --all` / `push --all` behind a per-rule flag.
- **SSH auth.** Key storage and host-key trust policy.
- **Forge metadata.** Issues/PRs/releases as a JSON export — a different
  product from a mirror, and a separate rule operation.

## Verification

Per-project, because `dotnet test` with several project paths in one
invocation fails with `MSB1008` on SDK 9.0.308 (the multi-project form in
`CLAUDE.md` does not work here):

```bash
git submodule update --init --recursive   # external/OAuth2; a fresh worktree has it empty
dotnet build Backer.sln
dotnet test tests/Hannibal.Tests/
dotnet test tests/WorkerRClone.Tests/
dotnet test tests/Tools.Tests/
dotnet test tests/WorkerGit.Tests/            # from Gate D
dotnet test tests/Hannibal.IntegrationTests/  # needs local PostgreSQL
```

Standing constraints, inherited from `docs/TESTING.md`:

- No test touches live backup data, starts rclone, or writes to a real cloud
  or forge account — except Gate H, which is opt-in via environment variable.
- Gates D and E test against bare repositories in temp directories, so the
  entire mirror engine is verifiable offline.
- Every destructive scenario asserts the destination is **unchanged** after a
  guard trips, not merely that the job failed.
- No acceptance criterion may assume an in-process `BackerAgent` harness —
  none exists. Server-side behaviour is tested through `BackerApiFactory`;
  agent-side behaviour is tested against the engine's own classes, with the
  two integration points that cannot be reached that way (Gate A AC5's
  `rclone.conf` diff, Gate G AC1's UI flow) recorded as manual smoke results
  in this document.

## Review history

Revision 1 was reviewed adversarially against the code at `3d9d354`. Findings
that changed the design, not merely the wording:

1. **`refs/*` + `push --mirror` fails against GitHub destinations** because of
   `refs/pull/*` hidden refs. → §3, explicit MIRRORED namespaces, no
   `--mirror`. Gate H AC2 now requires a source with an open PR.
2. **The destination-side adopt marker deleted itself** on every mirror push,
   and a source could spoof it via the `refs/*` fetch. → §5, marker outside
   MIRRORED with identity in the ref name.
3. **The guards missed force-push**, the most damaging attack, since ref count
   is unchanged. → §5 force-push guard; `Copy`'s refspec de-forced.
4. **The agent writes every storage into `rclone.conf` at startup**
   (`RCloneService.cs:1101-1201`), so a git storage would have leaked in
   regardless of the job path. → Gate A now filters the storage list.
5. **The 120 s job timeout** (`HannibalServiceJobs.cs:205-222`) would kill any
   initial clone and allow a second agent onto the same destination. → §6
   heartbeat requirement, Gate D AC8.
6. **The persisted fingerprint could report green while the destination was
   stale.** → §4, compare source against destination directly, no persisted
   state.
7. **Scheduling was cited against `BackofficeService`, which is dead code**
   (registration commented out at `Client/DependencyInjection.cs:40`), and
   leaned on `MaxTimeAfterSourceModification`, which no scheduler consumes.
   → §Context and Gate F restated against `RuleScheduler`/`ScheduleCalculator`.
8. **`Endpoint.Name` is not derived** — the constructor at `Endpoint.cs:22`
   has no callers, and names are neither generated nor unique.
9. **`Technology` and rules are not validated at all today**, so Gate A/B
   create validation rather than extend it — and a git rule could otherwise go
   live between Gate A and Gate D. → explicit ordering-safety note.
10. **Gate E needs a migration** for the per-rule overrides; the "no
    migration" claim holds only for Gate A.
11. Missing operational concerns now covered: cache locking, `git gc`, quota,
    disk-full vs corruption, subprocess watchdog, `Nop`, abort routing,
    shutdown parity, `GIT_TERMINAL_PROMPT`/`GIT_CONFIG_NOSYSTEM`/ambient
    credential helpers, push size limits, `--atomic`, default-branch
    propagation, guard alerting.
12. Test baseline corrected from an invented "28" to a measured 78.
