# Plan: Mid-Job OAuth2 Token Refresh

## The Problem

Long-running rclone sync jobs (e.g., OneDrive-to-Dropbox) can outlive the OAuth2 access token lifetime (~1 hour for Microsoft). The token is valid when the job starts, but expires mid-job. Since rclone reads its config at startup and doesn't dynamically reload tokens, it keeps hitting 401s from the provider API (e.g., Microsoft Graph), accumulating errors with no way to recover.

The existing `_checkOAuth2InactivityTimeoutAsync` eventually detects the symptom (expired token + no transfer activity for 5 minutes) but only **fails the job** — losing all progress and requiring a full retry.

## What Already Exists

| Component | Location | Status |
|-----------|----------|--------|
| Pre-job token refresh | `RCloneService._ensureConfiguredEndpoint` → `EnsureTokensValidAsync` | Works, but only at job start |
| OAuth2 inactivity timeout | `RCloneService._checkOAuth2InactivityTimeoutAsync` | Detects expired token but only kills the job |
| `ConfigSetAsync` | `RCloneClient.ConfigSetAsync` → rclone `/config/set` API | **Exists but is never called** — can push config updates to running rclone |
| `RefreshTokensAsync` | `OAuthStorageProviderBase.RefreshTokensAsync` | Works correctly to obtain new tokens |
| `BuildTokenJson` | `OAuthStorageProviderBase.BuildTokenJson` | Builds the token JSON blob rclone expects |

## Proposed Solution: Mid-Job Token Refresh

Instead of aborting and retrying, **refresh the token and push it to the running rclone process** — right inside the existing monitoring loop. All the pieces exist; they just need to be connected.

### Implementation Steps

#### 1. Add a token refresh method to `RCloneService`

Create `_tryRefreshOAuth2TokenAsync(RunningJobInfo runningJobInfo, CancellationToken ct)`:

1. Identify which endpoint(s) have expired tokens (source, destination, or both)
2. For each expired endpoint:
   a. Call `_rcloneStorages.EnsureTokensValidAsync(storage)` to refresh via the OAuth2 provider
   b. Get the updated `StorageState` with new `RCloneParameters`
   c. Use `ConfigSetAsync(remoteUriSchema, "token", newTokenJson)` to push the new token to the running rclone process
   d. Update the config file on disk for consistency (`_configManager.AddOrUpdateRemote` + `SaveToFile`)
3. Return `true` if refresh succeeded, `false` if it failed

#### 2. Modify `_checkOAuth2InactivityTimeoutAsync` (or the caller)

In the monitoring loop in `_checkFinishedJobs` (around line 312-339), when `_checkOAuth2InactivityTimeoutAsync` detects an expired token:

- **Before** timing out the job, attempt `_tryRefreshOAuth2TokenAsync`
- If refresh succeeds: reset `LastActivityAt`, log the refresh, and **don't timeout** — let rclone retry with the new token
- If refresh fails: fall through to the existing timeout/abort behavior

#### 3. Add rate limiting for refresh attempts

- Track the last refresh attempt time per `RunningJobInfo` (add a `LastTokenRefreshAttempt` field)
- Don't attempt a refresh more than once every 2 minutes to avoid hammering the OAuth provider
- After N failed refresh attempts (e.g., 3), give up and let the timeout proceed

### Code Flow (Modified Monitoring Loop)

```
_checkFinishedJobs():
  for each running job:
    GET /job/status
    if finished:
      report success/failure as before
    else:
      if hasExpiredToken:
        if canAttemptRefresh (rate limited):
          success = _tryRefreshOAuth2TokenAsync(...)
          if success:
            reset activity timer, continue monitoring
            continue  // skip timeout check

      check OAuth2 inactivity timeout (existing logic, unchanged)
      report Executing heartbeat
```

### Files to Modify

| File | Change |
|------|--------|
| `worker/WorkerRClone/Services/RCloneService.cs` | Add `_tryRefreshOAuth2TokenAsync`, modify monitoring loop |
| `worker/WorkerRClone/Services/RunningJobInfo.cs` | Add `LastTokenRefreshAttempt` and `TokenRefreshAttempts` fields |

### Edge Cases

- **Both endpoints expired**: Refresh both independently
- **Refresh token itself expired**: `RefreshTokensAsync` will throw → fall through to timeout behavior, user must re-authenticate via the web UI
- **Rclone doesn't pick up `/config/set`**: rclone should use the updated token for subsequent API calls; if not, the inactivity timeout still acts as a safety net
- **Concurrent jobs sharing a storage**: Token refresh on one job benefits all jobs using the same remote, since `/config/set` updates the in-memory config globally in the rclone process

### Benefits

- Long-running jobs survive token expiration transparently
- No job progress is lost (e.g., the 2367 files already transferred continue)
- Leverages existing infrastructure (`ConfigSetAsync`, `EnsureTokensValidAsync`)
- Falls back to existing timeout behavior if refresh fails
- Minimal code changes (~50 lines)
