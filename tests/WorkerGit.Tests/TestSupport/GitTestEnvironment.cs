using WorkerGit.Configuration;
using WorkerGit.Services;

namespace WorkerGit.Tests.TestSupport;

/**
 * A throwaway temp directory plus a ready-to-use <see cref="GitWorkerOptions"/>
 * pointed at it. Every test gets its own instance so tests never share a
 * cache root or a lock file.
 */
public sealed class GitTestEnvironment : IDisposable
{
    public string RootDir { get; }

    public string CacheRoot { get; }

    public GitWorkerOptions Options { get; }

    public GitTestEnvironment(
        TimeSpan? jobTimeout = null, TimeSpan? stallTimeout = null, string gitPath = "git")
    {
        RootDir = Directory.CreateTempSubdirectory("WorkerGit.Tests.").FullName;
        CacheRoot = Path.Combine(RootDir, "cache");
        Directory.CreateDirectory(CacheRoot);

        Options = new GitWorkerOptions
        {
            GitPath = gitPath,
            CacheRoot = CacheRoot,
            JobTimeout = jobTimeout ?? TimeSpan.FromSeconds(60),
            StallTimeout = stallTimeout ?? TimeSpan.FromSeconds(30)
        };
    }

    /** A fresh path under the environment's root, not yet created. */
    public string NewPath(string name) => Path.Combine(RootDir, name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RootDir))
            {
                Directory.Delete(RootDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort - a lingering handle on Windows must not fail the test run.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
