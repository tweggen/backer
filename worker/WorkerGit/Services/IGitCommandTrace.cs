namespace WorkerGit.Services;

/**
 * One recorded git invocation (plan-git-repo-storage.md §6). Deliberately
 * excludes the process environment - credentials travel only through env
 * variables (never argv), and a trace that captured env would defeat the
 * point of AC7's "no secret leakage" check.
 */
public sealed record GitCommandRecord(
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    int? ExitCode,
    TimeSpan Duration,
    GitRunOutcome Outcome,
    string? Message);

/**
 * Sink for <see cref="GitCommandRecord"/>s, injected into
 * <see cref="GitCliRunner"/> so tests can assert on exactly which git
 * commands ran (Gate D AC1, AC3, AC11) without parsing log text.
 */
public interface IGitCommandTrace
{
    void Record(GitCommandRecord record);
}

/**
 * Default trace used when a caller does not need one. Production code always
 * gets a real one from the hosted slice; this exists so
 * <see cref="GitCliRunner"/> never has to null-check its sink.
 */
public sealed class NullGitCommandTrace : IGitCommandTrace
{
    public static readonly NullGitCommandTrace Instance = new();

    private NullGitCommandTrace()
    {
    }

    public void Record(GitCommandRecord record)
    {
    }
}

/**
 * A trace that keeps every record in memory, in order. The house double for
 * tests (mirrors <c>TestSupport.RClone.RCloneStub</c>'s recorded-request
 * list) - thread-safe because Gate D AC6 runs invocations concurrently.
 */
public sealed class RecordingGitCommandTrace : IGitCommandTrace
{
    private readonly object _lock = new();
    private readonly List<GitCommandRecord> _records = new();

    public IReadOnlyList<GitCommandRecord> Records
    {
        get { lock (_lock) { return _records.ToArray(); } }
    }

    public void Record(GitCommandRecord record)
    {
        lock (_lock)
        {
            _records.Add(record);
        }
    }
}
