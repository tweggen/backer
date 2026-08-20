namespace TestSupport.RClone;

/// <summary>
/// How the stub should behave for one rclone job.
/// </summary>
public sealed class StubJobBehaviour
{
    /// <summary>
    /// Number of <c>job/status</c> polls that report the job as still running
    /// before it completes. 0 (the default) means the very first poll already
    /// reports it finished.
    /// </summary>
    public int CompleteAfterPolls { get; set; }

    /// <summary>
    /// When true the job never reports finished, however often it is polled.
    /// Used for stall and timeout scenarios.
    /// </summary>
    public bool NeverFinish { get; set; }

    /// <summary>
    /// When set, the job completes as a failure carrying this error text.
    /// </summary>
    public string? Error { get; set; }

    public StubJobBehaviour Clone() => new()
    {
        CompleteAfterPolls = CompleteAfterPolls,
        NeverFinish = NeverFinish,
        Error = Error
    };
}

/// <summary>
/// Scripting surface of <see cref="RCloneStub"/>. Every knob here changes what
/// the stub does next; nothing here blocks or sleeps.
/// </summary>
public sealed class RCloneStubScript
{
    private readonly object _lock = new();
    private readonly Queue<StubJobBehaviour> _queued = new();

    /// <summary>
    /// Behaviour applied to any job for which nothing more specific was
    /// enqueued. Defaults to "succeeds on the first poll".
    /// </summary>
    public StubJobBehaviour Default { get; } = new();

    /// <summary>
    /// Behaviour for the next job started, ahead of <see cref="Default"/>.
    /// Calls queue up, so two calls script the next two jobs in order.
    /// </summary>
    public RCloneStubScript EnqueueJob(Action<StubJobBehaviour> configure)
    {
        var behaviour = Default.Clone();
        configure(behaviour);

        lock (_lock)
        {
            _queued.Enqueue(behaviour);
        }

        return this;
    }

    /// <summary>Next job fails with <paramref name="error"/>.</summary>
    public RCloneStubScript EnqueueFailure(string error) =>
        EnqueueJob(b => b.Error = error);

    /// <summary>Next job never finishes.</summary>
    public RCloneStubScript EnqueueStall() =>
        EnqueueJob(b => b.NeverFinish = true);

    /// <summary>Next job reports running for <paramref name="polls"/> polls, then succeeds.</summary>
    public RCloneStubScript EnqueueSlowSuccess(int polls) =>
        EnqueueJob(b => b.CompleteAfterPolls = polls);

    internal StubJobBehaviour TakeNext()
    {
        lock (_lock)
        {
            return _queued.Count > 0 ? _queued.Dequeue() : Default.Clone();
        }
    }
}
