namespace WorkerGit.Services;

/**
 * How a single git invocation ended.
 */
public enum GitRunOutcome
{
    /** The process exited on its own; check <see cref="GitRunResult.ExitCode"/>. */
    Completed,

    /** Killed because <see cref="Configuration.GitWorkerOptions.JobTimeout"/> elapsed. */
    TimedOut,

    /** Killed because no output line arrived within <see cref="Configuration.GitWorkerOptions.StallTimeout"/>. */
    Stalled,

    /** Killed because the caller's <see cref="CancellationToken"/> fired. */
    Cancelled
}

/**
 * Outcome of one <see cref="GitCliRunner.RunAsync"/> call.
 */
public sealed class GitRunResult
{
    public required GitRunOutcome Outcome { get; init; }

    /** Only meaningful when <see cref="Outcome"/> is <see cref="GitRunOutcome.Completed"/>. */
    public int ExitCode { get; init; }

    public string StdOut { get; init; } = string.Empty;

    public string StdErr { get; init; } = string.Empty;

    /** Set for TimedOut/Stalled/Cancelled, naming which timer fired. */
    public string? Message { get; init; }

    /** True only for a clean exit with code 0. */
    public bool Success => Outcome == GitRunOutcome.Completed && ExitCode == 0;
}

/**
 * Credentials for one git invocation. Never written to disk, never placed in
 * argv - carried to the child process only via the environment variables the
 * askpass helper reads (plan §6).
 */
public sealed class GitCredentials
{
    public string Username { get; init; } = string.Empty;

    public string Token { get; init; } = string.Empty;
}
