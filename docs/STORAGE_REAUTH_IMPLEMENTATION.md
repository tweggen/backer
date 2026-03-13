# Smart Storage Reauthentication Implementation

## Overview

This document describes the implementation of intelligent storage reauthentication handling across the Backer system. The feature allows storage credentials to be updated in the frontend, and automatically notifies worker agents to restart rclone **only if the configuration actually changed**.

## Architecture

### Three-Layer Implementation

1. **Frontend (Poe/Blazor)** - User re-authenticates storage
2. **Backend (Hannibal)** - Detects token changes, broadcasts SignalR event
3. **Agent (WorkerRClone)** - Receives event, decides if restart needed, executes smart restart

## Flow Diagram

```
User clicks "Authenticate" in UI
    ↓
OAuth flow completes
    ↓
Backend: UpdateStorageAsync() 
    ↓
Tokens changed? 
    ├─ No  → Return (no broadcast)
    └─ Yes → SignalR.Broadcast("StorageReauthenticated", uriSchema)
               ↓
           Worker receives event
               ↓
           Compare current config vs new config
               ↓
           Config changed?
               ├─ No  → Continue running (no restart)
               └─ Yes → State Machine: RestartingForReauth
                           ↓
                       1. Stop running jobs
                       2. Kill rclone process
                       3. Clear HTTP client
                       4. Clear config manager
                       5. Clear storage states
                       6. Reload storage list
                           ↓
                       BackendsLoggingIn → CheckRCloneProcess → StartRCloneProcess → WaitStart → Running
```

## Changes by Component

### 1. Worker Agent (WorkerRClone)

#### New State & Events

**ServiceEvent.cs**
- Added: `StorageReauthenticationRequired`
- Added: `ReauthCleanupComplete`

**RCloneServiceState.cs**
- Added state: `RestartingForReauth` - intermediate state for cleanup before re-login

#### State Machine (RCloneStateMachine.cs)

Added `StorageReauthenticationRequired` transition from:
- `BackendsLoggingIn`
- `CheckRCloneProcess`
- `StartRCloneProcess`
- `WaitStart`
- `Running`

New state configuration:
```csharp
[RCloneServiceState.ServiceState.RestartingForReauth] = new()
{
    State = RCloneServiceState.ServiceState.RestartingForReauth,
    OnEnter = async () => await _service._handleStorageReauthImpl(),
    Transitions = new()
    {
        [ServiceEvent.ReauthCleanupComplete] = RCloneServiceState.ServiceState.BackendsLoggingIn
    }
}
```

#### RCloneConfigManager.cs

New methods for configuration comparison:
- `ExportToString()` - Export config to string for comparison
- `LoadFromString(string)` - Load config from string
- Existing `GetRemote(string)` - Retrieve specific remote config

#### RCloneStorages.cs

Enhanced `FindStorageState()`:
```csharp
public async Task<StorageState> FindStorageState(
    Storage storage, 
    CancellationToken cancellationToken,
    bool forceRefresh = false)  // NEW: Force cache refresh
```

New method:
```csharp
public void ClearStorageStates()  // Clear all cached states
```

#### RCloneService.cs

**SignalR Event Handler** (in `_startRunningImpl()`):
```csharp
_hannibalConnection.On<string>("StorageReauthenticated", async (storageUriSchema) =>
{
    // Check if restart is actually needed
    bool restartRequired = await _doesStorageChangeRequireRestart(storageUriSchema);
    
    if (!restartRequired)
    {
        // No changes, continue normally
        return;
    }
    
    // Trigger restart
    await _stateMachine.TransitionAsync(ServiceEvent.StorageReauthenticationRequired);
});
```

**New Implementation Methods**:

1. `_handleStorageReauthImpl()` - Complete cleanup and restart flow
   - Stops all running jobs
   - Kills rclone process
   - Disposes HTTP client
   - Clears job mappings
   - Resets config manager
   - Clears storage state cache
   - Reloads storage list from backend
   - Transitions to BackendsLoggingIn

2. `_doesStorageChangeRequireRestart(string storageUriSchema)` - Smart comparison
   - Retrieves current config from RCloneConfigManager
   - Fetches new storage state with fresh tokens (forceRefresh: true)
   - Compares parameters
   - Returns true only if config actually changed

3. `_areRCloneParametersEqual()` - Parameter comparison
   - Compares dictionary keys and values
   - Case-sensitive comparison for tokens

### 2. Backend (Hannibal)

#### HannibalServiceStorages.cs

Modified `UpdateStorageAsync()`:
```csharp
public async Task<Storage> UpdateStorageAsync(
    int id,
    Storage updatedStorage,
    CancellationToken cancellationToken)
{
    // ... existing update logic ...
    
    // Track if tokens changed
    bool tokensChanged = false;
    if (!string.IsNullOrEmpty(updatedStorage.AccessToken) && 
        storage.AccessToken != updatedStorage.AccessToken)
    {
        tokensChanged = true;
    }
    if (!string.IsNullOrEmpty(updatedStorage.RefreshToken) && 
        storage.RefreshToken != updatedStorage.RefreshToken)
    {
        tokensChanged = true;
    }
    
    // ... save changes ...
    
    // Notify agents only if tokens changed
    if (tokensChanged)
    {
        _logger.LogInformation($"Storage {storage.UriSchema} tokens updated, notifying agents");
        await _hannibalHub.Clients.All.SendAsync(
            "StorageReauthenticated", 
            storage.UriSchema, 
            cancellationToken);
    }
    
    return storage;
}
```

### 3. Frontend (Poe/Blazor)

#### Storages.razor

**No changes required!** The existing OAuth flow already:
1. Calls `TriggerOAuth2Async()` to start OAuth
2. OAuth completes and calls `ProcessOAuth2ResultAsync()`
3. Which calls `UpdateStorageAsync()` with new tokens
4. Which now triggers the SignalR broadcast automatically

## Key Features

### 1. Smart Restart Decision

The system avoids unnecessary restarts by:
- Comparing current rclone configuration with new configuration
- Only restarting if tokens/parameters actually changed
- If user re-authenticates with same account → no restart
- If tokens are refreshed but still valid → no restart

### 2. State Machine Integration

The restart flow is fully integrated into the state machine:
- Can be triggered from any operational state
- Uses event queue if state doesn't allow immediate handling
- Follows proper state transition flow
- Maintains all safety guarantees

### 3. Comprehensive Cleanup

When restart is required:
1. Gracefully stops all running jobs
2. Kills rclone process
3. Disposes resources (HTTP clients)
4. Clears all caches (job mappings, storage states, config)
5. Reloads fresh data from backend
6. Re-logs in to all backends with new tokens

### 4. Error Handling

- Graceful degradation if job stopping fails
- Continues restart even if cleanup partially fails
- Logs detailed information at each step
- On comparison error, defaults to safe restart

## Testing Scenarios

### Scenario 1: Tokens Actually Changed
1. User re-authenticates storage in UI
2. Backend saves new tokens
3. Backend broadcasts "StorageReauthenticated"
4. Worker compares configs → detects change
5. Worker transitions to RestartingForReauth
6. Worker cleans up and restarts rclone
7. Jobs resume with new tokens

**Expected Log Output**:
```
[INFO] RCloneService: Received storage reauthentication event for dropbox-personal
[INFO] RCloneService: Checking if storage dropbox-personal requires restart
[INFO] RCloneService: Storage dropbox-personal parameters changed, restart required
[INFO] State transition: Running -> RestartingForReauth (event: StorageReauthenticationRequired)
[INFO] RCloneService: Handling storage reauthentication - cleaning up...
[INFO] RCloneService: Stopping job 123 for reauth
[INFO] RCloneService: Killing rclone process for reauth
[INFO] RCloneService: Clearing rclone configuration
[INFO] RCloneStorages: Cleared all storage states for reauth
[INFO] RCloneService: Reloaded storage list with updated tokens
[INFO] RCloneService: Cleanup complete, proceeding to backends login
[INFO] State transition: RestartingForReauth -> BackendsLoggingIn (event: ReauthCleanupComplete)
```

### Scenario 2: Tokens Unchanged (No Restart)
1. User re-authenticates storage in UI
2. Backend saves tokens (same as before)
3. Backend does NOT broadcast (no token change)
4. Worker continues normally
5. No interruption to jobs

**Expected Log Output**:
```
(No logs - backend doesn't broadcast if tokens unchanged)
```

### Scenario 3: Token Refresh (No Restart)
1. OAuth client refreshes access token automatically
2. Refresh token unchanged
3. Backend saves new access token
4. Backend broadcasts "StorageReauthenticated"
5. Worker compares configs → token object contains same data
6. Worker continues normally (JSON token structure unchanged)

**Expected Log Output**:
```
[INFO] RCloneService: Received storage reauthentication event for onedrive-work
[INFO] RCloneService: Checking if storage onedrive-work requires restart
[INFO] RCloneService: Storage onedrive-work parameters unchanged, no restart needed
```

### Scenario 4: Restart During Job Execution
1. Worker is running jobs
2. User re-authenticates storage
3. Worker receives event
4. Worker gracefully stops running jobs
5. Worker restarts rclone
6. Jobs are re-queued by Hannibal

**Expected Log Output**:
```
[INFO] RCloneService: Received storage reauthentication event for dropbox-personal
[INFO] RCloneService: Storage dropbox-personal parameters changed, restart required
[INFO] State transition: Running -> RestartingForReauth
[INFO] RCloneService: Stopping job 456 for reauth
[INFO] RCloneService: Stopping job 789 for reauth
[INFO] RCloneService: Killing rclone process for reauth
...
```

## Benefits

1. **Minimal Disruption**: Only restarts when actually necessary
2. **Efficient**: No wasted CPU cycles restarting rclone unnecessarily
3. **Safe**: Gracefully stops jobs before restart
4. **Transparent**: Detailed logging of all decisions
5. **Reliable**: Proper error handling and state management
6. **Smart**: Compares actual configuration, not just events

## Future Enhancements

Potential improvements:
1. **Granular Restart**: Only restart if specific storage is in use
2. **Job Preservation**: Save job state and resume after restart
3. **Graceful Finish**: Allow current jobs to finish before restart (with timeout)
4. **Metrics**: Track restart frequency and reasons
5. **Notification**: Inform user when restart occurs

## Files Modified

### Agent (WorkerRClone)
- `Services/ServiceEvent.cs` - Added 2 new events
- `Models/RCloneServiceState.cs` - Added RestartingForReauth state
- `Services/RCloneStateMachine.cs` - Added state config and transitions
- `Services/RCloneConfigManager.cs` - Added ExportToString, LoadFromString
- `Services/RCloneStorages.cs` - Added forceRefresh parameter, ClearStorageStates
- `Services/RCloneService.cs` - Added SignalR handler, 3 new methods, updated ExecuteAsync

### Backend (Hannibal)
- `Services/HannibalServiceStorages.cs` - Modified UpdateStorageAsync to broadcast

### Frontend (Poe)
- No changes required (OAuth flow already correct)

## Deployment Notes

1. Deploy backend first (adds SignalR broadcast)
2. Deploy agents (adds event handling)
3. No frontend changes needed
4. No database migrations required
5. Backward compatible (old agents ignore new events)

## Conclusion

This implementation provides intelligent storage reauthentication that:
- Minimizes disruption to running jobs
- Only restarts when configuration actually changes
- Maintains proper state machine flow
- Provides comprehensive logging and error handling
- Works seamlessly across all three system layers
