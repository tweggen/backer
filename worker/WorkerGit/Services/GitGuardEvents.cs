using Microsoft.Extensions.Logging;

namespace WorkerGit.Services;

/**
 * Distinct <see cref="EventId"/>s for each of §5's guards
 * (plan-git-repo-storage.md Gate E AC9). A tripped guard is a security
 * signal, not a routine failure - it is logged at
 * <see cref="LogLevel.Warning"/> under one of these ids specifically so
 * alerting can key on "which guard" without parsing message text.
 */
public static class GitGuardEvents
{
    public static readonly EventId ZeroRef = new(5101, nameof(ZeroRef));
    public static readonly EventId SelfMirror = new(5102, nameof(SelfMirror));
    public static readonly EventId AdoptUnmarked = new(5103, nameof(AdoptUnmarked));
    public static readonly EventId AdoptMismatch = new(5104, nameof(AdoptMismatch));
    public static readonly EventId Shrink = new(5105, nameof(Shrink));
    public static readonly EventId ForcePush = new(5106, nameof(ForcePush));
}
