namespace WorkerRClone.Services;

/// <summary>
/// Events that trigger state transitions in the RClone service
/// </summary>
public enum ServiceEvent
{
    ConfigReceived,
    ConfigInvalid,
    OnlineCheckPassed,
    OnlineCheckFailed,
    BackendsLoggedIn,
    RCloneProcessFound,
    RCloneProcessNotFound,
    RCloneProcessStarted,
    RCloneProcessStartFailed,
    StartRequested,
    StopRequested,
    JobsCompleted,
    StorageReauthenticationRequired,
    ReauthCleanupComplete
}
