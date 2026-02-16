# Mid-Job OAuth2 Token Refresh

**Status: Implemented** (commit 95bc93f, 2026-02-16)

## The Problem

Long-running rclone sync jobs (e.g., OneDrive-to-Dropbox) can outlive the OAuth2 access token lifetime (~1 hour for Microsoft). The token is valid when the job starts, but expires mid-job. Since rclone reads its config at startup and doesn't dynamically reload tokens, it keeps hitting 401s from the provider API (e.g., Microsoft Graph), accumulating errors with no way to recover.

The existing `_checkOAuth2InactivityTimeoutAsync` eventually detects the symptom (expired token + no transfer activity for 5 minutes) but only **fails the job** — losing all progress and requiring a full retry.

## What Already Exists

| Component | Location | Status |
|-----------|----------|--------|
| Pre-job token refresh | `RCloneService._ensureConfiguredEndpoint` → `EnsureTokensValidAsync` | Works, but only at job start |
| OAuth2 inactivity timeout | `RCloneService._checkOAuth2InactivityTimeoutAsync` | Detects expired token but only kills the job |
| Full reauth restart flow | `RCloneService._handleStorageReauthImpl` → `RestartingForReauth` state | Works, but triggered by SignalR from web UI, not by expired tokens during jobs |
| `RefreshTokensAsync` | `OAuthStorageProviderBase.RefreshTokensAsync` | Works correctly to obtain new tokens |
| Config file write | `RCloneConfigManager.AddOrUpdateRemote` + `SaveToFile` | Atomic write via temp file |

### What Does NOT Exist

- **No rclone RC API to hot-update tokens.** The `/config/set` endpoint does not exist. The `/config/update` endpoint is designed for interactive provider setup flows, not for programmatically setting credentials. The existing `ConfigSetAsync` in `RCloneClient.cs` is dead code that would 404 at runtime.
- **No way to update a running rclone process's tokens.** Rclone reads config at startup only. The only option is to restart the rclone process.

## Solution: Restart-Based Mid-Job Token Refresh

When the monitoring loop detects an expired token on a running job, the system refreshes the tokens and restarts rclone. This reuses the existing `RestartingForReauth` machinery but triggers it from token expiry detection rather than from a SignalR event.

Since rclone sync is idempotent (it compares source and destination), restarted jobs resume from where they left off — already-transferred files are skipped.

### What Was Implemented

#### 1. Mid-job token refresh in the monitoring loop

In `_checkFinishedJobs`, when `_checkOAuth2InactivityTimeoutAsync` detects an expired token with stalled transfers, the new `_tryMidJobTokenRefreshAsync` method is called before falling through to the existing timeout/fail behavior:

- Attempts to refresh the token via `_rcloneStorages.EnsureTokensValidAsync(storage)` for each expired endpoint
- If refresh succeeds: reports all running jobs to Hannibal as `DoneFailure`, then triggers `StorageReauthenticationRequired` on the state machine
- If refresh fails (e.g., refresh token itself expired): falls through to existing timeout/abort behavior

#### 2. Job reporting during storage reauthentication

`_handleStorageReauthImpl` now reports all interrupted jobs to Hannibal as `DoneFailure` before clearing `_runningJobs`. This benefits both the mid-job token refresh path and the existing SignalR-triggered reauth path (previously jobs were silently dropped).

The RuleScheduler re-creates jobs from active Rules once the service returns to `Running` state.

#### 3. Rate limiting

- `_lastTokenRefreshRestart` timestamp: prevents restarts within 5 minutes of each other
- `_tokenRefreshRestartCount`: limits to 3 consecutive restarts without a successful job completion
- Counter resets to 0 when any job completes successfully

#### 4. Dead code removal

- Removed `ConfigSetAsync` from `RCloneClient.cs` (`/config/set` doesn't exist in rclone RC API)
- Deleted `JobConfigSetParams.cs` (only used by `ConfigSetAsync`)

### Code Flow

```
_checkFinishedJobs():
  for each running job:
    GET /job/status
    if finished:
      report success/failure as before
    else:
      if hasExpiredToken AND noTransferActivity:
        if canAttemptRestart (rate limited):
          refreshed = EnsureTokensValidAsync(storage)
          if refreshed:
            report all running jobs to Hannibal as failed (retriable)
            _stateMachine.TransitionAsync(StorageReauthenticationRequired)
            return  // existing reauth flow handles the rest
          else:
            fall through to existing timeout behavior

      check OAuth2 inactivity timeout (existing logic, unchanged)
      report Executing heartbeat
```

### Restart Sequence (existing flow, reused)

```
StorageReauthenticationRequired
  → RestartingForReauth
    → _handleStorageReauthImpl():
        1. StopJobAsync() for all running rclone jobs
        2. Kill rclone process
        3. Dispose HTTP client
        4. Clear _runningJobs
        5. Reset config manager, write empty config
        6. Clear storage states
        7. Reload storages from Hannibal (with fresh tokens)
  → ReauthCleanupComplete
    → BackendsLoggingIn
      → _backendsLoginImpl(): rebuild config with new tokens, write to disk
    → BackendsLoggedIn
      → CheckRCloneProcess (none found, was killed)
        → StartRCloneProcess (launch with new config)
          → WaitStart
            → Running (autostart re-queues jobs)
```

### Files Modified

| File | Change |
|------|--------|
| `worker/WorkerRClone/Services/RCloneService.cs` | Added `_tryMidJobTokenRefreshAsync` method, rate-limit fields, modified `_checkFinishedJobs` to attempt refresh before timeout, added job reporting in `_handleStorageReauthImpl` |
| `worker/WorkerRClone/Client/RCloneClient.cs` | Removed dead `ConfigSetAsync` method |
| `worker/WorkerRClone/Client/Models/JobConfigSetParams.cs` | Deleted (dead code) |

### Edge Cases

- **Both endpoints expired**: Only one restart needed — `_backendsLoginImpl` refreshes all storages
- **Refresh token itself expired**: `EnsureTokensValidAsync` returns `IsNowValid = false` → fall through to timeout, user must re-authenticate via web UI
- **Multiple jobs affected**: One restart fixes all jobs since all storages are re-initialized. Report all running jobs to Hannibal before restarting
- **Concurrent SignalR reauth + token expiry reauth**: Both trigger the same `StorageReauthenticationRequired` event. The state machine ensures only one restart happens; the second event is ignored if already in `RestartingForReauth`
- **Job progress**: rclone sync is idempotent — already-transferred files are skipped on restart. The only "lost" work is any partially-transferred file in progress at kill time

### Benefits

- Long-running jobs survive token expiration with minimal disruption
- Reuses the battle-tested `RestartingForReauth` flow — no new state machine states needed
- rclone sync idempotency means minimal wasted transfer work
- Falls back to existing timeout behavior if refresh fails
- Cleaned up dead code (`ConfigSetAsync`, `JobConfigSetParams`)

### Trade-offs

- Brief downtime during restart (~5-10 seconds) where no transfers happen
- Any file partially transferred at kill time must restart from scratch (but completed files are preserved)
- Restart affects ALL running jobs, not just the one with the expired token

### Verification

- `dotnet build Backer.sln` — 0 errors
- `dotnet test tests/WorkerRClone.Tests/` — 14/14 tests passed
- No remaining references to `ConfigSetAsync` or `JobConfigSetParams`
