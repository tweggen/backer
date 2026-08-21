namespace WorkerGit.Configuration;

/**
 * Options for the git mirror engine (plan-git-repo-storage.md §6, §7). Gate D
 * owns only what the offline engine and its tests need; the hosted slice
 * (acquisition, heartbeat, capability probe) adds its own options later.
 */
public sealed class GitWorkerOptions
{
    /**
     * Path or bare name of the git executable, resolved by the shell/PATH
     * lookup the process uses when it is not rooted. Tests point this at a
     * stub executable to exercise the watchdog without a real hang.
     */
    public string GitPath { get; set; } = "git";

    /**
     * Root directory for the bare mirror cache (plan §7). Nullable here
     * because the hosted slice picks a default under the agent's data
     * directory; every offline test sets it explicitly to a temp directory.
     */
    public string? CacheRoot { get; set; }

    /**
     * Overall ceiling on a single git invocation, regardless of progress
     * (plan §6 "Watchdog"). A stalled network must not hang a job forever.
     */
    public TimeSpan JobTimeout { get; set; } = TimeSpan.FromHours(2);

    /**
     * Ceiling on the gap between two lines of output from a git invocation.
     * Any stdout or stderr line resets this timer - it catches a connection
     * that is open but silent, which JobTimeout alone would not notice for
     * up to two hours.
     */
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /**
     * Free-space floor checked before the corruption-recovery re-clone
     * (plan §7 "Disposable, but not blindly"). Below this, a failed fetch is
     * reported as disk-full instead of triggering a delete-and-reclone that
     * would need even more space than the fetch that just failed.
     */
    public long MinFreeDiskBytes { get; set; } = 1L * 1024 * 1024 * 1024;
}
