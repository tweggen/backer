using System.Text;
using WorkerGit.Configuration;

namespace WorkerGit.Services;

/**
 * Probes free disk space ahead of the corruption-recovery re-clone
 * (plan-git-repo-storage.md §7). An injectable seam because "the disk is
 * full" is otherwise unreproducible in a test.
 */
public interface IFreeDiskSpaceProbe
{
    long GetFreeBytes(string path);
}

/** Real free-space probe, backed by <see cref="DriveInfo"/>. */
public sealed class DriveFreeDiskSpaceProbe : IFreeDiskSpaceProbe
{
    public long GetFreeBytes(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root))
        {
            return long.MaxValue;
        }

        return new DriveInfo(root).AvailableFreeSpace;
    }
}

/** One completed (acquire lock, ensure cache, fetch) round. */
public sealed class GitCacheFetchResult
{
    public required bool Success { get; init; }

    public required string CachePath { get; init; }

    /** True when the fetch only succeeded after a delete-and-reclone. */
    public bool RecoveredFromCorruption { get; init; }

    /** True when a corrupt-looking fetch failure was NOT recovered because free disk space was below the floor. */
    public bool DiskFull { get; init; }

    public string? Message { get; init; }
}

/** One (lock acquired, lock released) window, for the concurrency test (AC6). */
public sealed record GitCacheLockEvent(string CachePath, DateTimeOffset AcquiredAt, DateTimeOffset ReleasedAt);

public interface IGitCacheLockTrace
{
    void Record(GitCacheLockEvent lockEvent);
}

public sealed class NullGitCacheLockTrace : IGitCacheLockTrace
{
    public static readonly NullGitCacheLockTrace Instance = new();
    private NullGitCacheLockTrace() { }
    public void Record(GitCacheLockEvent lockEvent) { }
}

public sealed class RecordingGitCacheLockTrace : IGitCacheLockTrace
{
    private readonly object _lock = new();
    private readonly List<GitCacheLockEvent> _events = new();

    public IReadOnlyList<GitCacheLockEvent> Events
    {
        get { lock (_lock) { return _events.ToArray(); } }
    }

    public void Record(GitCacheLockEvent lockEvent)
    {
        lock (_lock) { _events.Add(lockEvent); }
    }
}

/**
 * Bare mirror cache, one directory per source repository, shared by every job
 * that mirrors from that source (plan §7). Locked per cache directory so two
 * jobs reading one source concurrently (allowed by the server,
 * <c>HannibalServiceJobs.cs:289-305</c>) never race a delete-and-reclone
 * against a fetch.
 */
public sealed class GitCacheManager
{
    private readonly GitWorkerOptions _options;
    private readonly GitCliRunner _runner;
    private readonly IFreeDiskSpaceProbe _freeDiskSpaceProbe;
    private readonly IGitCacheLockTrace _lockTrace;

    public GitCacheManager(
        GitWorkerOptions options,
        GitCliRunner runner,
        IFreeDiskSpaceProbe? freeDiskSpaceProbe = null,
        IGitCacheLockTrace? lockTrace = null)
    {
        _options = options;
        _runner = runner;
        _freeDiskSpaceProbe = freeDiskSpaceProbe ?? new DriveFreeDiskSpaceProbe();
        _lockTrace = lockTrace ?? NullGitCacheLockTrace.Instance;
    }

    /**
     * Deterministic cache path for one source repository:
     * <c>{CacheRoot}/{userId}/{sourceUriSchema}/{sanitised-path}.git</c>.
     * Pure - safe to call without holding the lock.
     *
     * <paramref name="sourcePath"/> is hashed rather than transliterated
     * verbatim: a literal sanitised copy of a long source identity (a full
     * URL, or a deeply nested local path under an already-long cache root)
     * routinely exceeded Windows' MAX_PATH once combined with CacheRoot and
     * git's own temp-file suffixes during a repack, observed empirically
     * while building Gate D's tests. A fixed-length hash keeps the path
     * short and still deterministic - same source identity, same directory,
     * which is all AC6's shared-cache locking needs.
     */
    public string GetCachePath(string userId, string sourceUriSchema, string sourcePath)
    {
        if (string.IsNullOrEmpty(_options.CacheRoot))
        {
            throw new InvalidOperationException("GitWorkerOptions.CacheRoot must be set.");
        }

        var sanitizedUserId = _sanitizeSegment(userId);
        var sanitizedSchema = _sanitizeSegment(sourceUriSchema);
        var identityHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath)))[..16]
            .ToLowerInvariant();

        return Path.Combine(_options.CacheRoot, sanitizedUserId, sanitizedSchema, identityHash + ".git");
    }

    /**
     * Acquires the per-cache lock, ensures the bare cache exists, and fetches
     * <paramref name="fetchRefspecs"/> from <paramref name="sourceRemoteUrl"/>
     * (plan §3: one explicit forced refspec per MIRRORED namespace - forcing
     * the cache-bound fetch is safe because the cache is disposable; §5's
     * no-force rule is about the push).
     *
     * On a failed fetch that <see cref="_looksLikeCorruptionAsync"/> confirms:
     * below <see cref="GitWorkerOptions.MinFreeDiskBytes"/> free -&gt; reports
     * disk-full without touching the cache; otherwise deletes the cache and
     * re-clones exactly once.
     */
    public async Task<GitCacheFetchResult> FetchAsync(
        string userId,
        string sourceUriSchema,
        string sourcePath,
        string sourceRemoteUrl,
        GitCredentials? credentials,
        IReadOnlyList<string> fetchRefspecs,
        Action<string>? onProgressLine,
        CancellationToken cancellationToken)
    {
        var cachePath = GetCachePath(userId, sourceUriSchema, sourcePath);

        await using var cacheLock = await _acquireLockAsync(cachePath, cancellationToken);

        await _ensureCacheInitializedAsync(cachePath, cancellationToken);

        var fetchArgs = _buildFetchArgs(sourceRemoteUrl, fetchRefspecs);
        var fetchResult = await _runner.RunAsync(
            fetchArgs, cachePath, credentials, onProgressLine, cancellationToken);

        /*
         * A corrupted pre-existing pack does not reliably fail `git fetch`
         * itself (confirmed empirically): if the newly transferred objects
         * are self-contained, git logs "not a GIT packfile" / "cannot be
         * accessed" for the broken pack and still exits 0. Gating recovery on
         * exit code alone would then never trigger, so a clean failure OR a
         * known corruption marker in stderr both route through the same
         * fsck-confirmed check below.
         */
        var fetchLooksTroubled = !fetchResult.Success || _mentionsCorruption(fetchResult.StdErr);
        if (!fetchLooksTroubled)
        {
            await _runner.RunAsync(new[] { "gc", "--auto" }, cachePath, null, null, cancellationToken);
            return new GitCacheFetchResult { Success = true, CachePath = cachePath };
        }

        if (!await _looksLikeCorruptionAsync(cachePath, cancellationToken))
        {
            if (fetchResult.Success)
            {
                // Exit 0 with a stray troubling stderr line fsck did not
                // confirm - fsck is the authority, not a substring match.
                await _runner.RunAsync(new[] { "gc", "--auto" }, cachePath, null, null, cancellationToken);
                return new GitCacheFetchResult { Success = true, CachePath = cachePath };
            }

            return new GitCacheFetchResult
            {
                Success = false,
                CachePath = cachePath,
                Message = $"fetch failed: {_firstLine(fetchResult.StdErr, fetchResult.Message)}"
            };
        }

        var freeBytes = _freeDiskSpaceProbe.GetFreeBytes(_options.CacheRoot!);
        if (freeBytes < _options.MinFreeDiskBytes)
        {
            return new GitCacheFetchResult
            {
                Success = false,
                CachePath = cachePath,
                DiskFull = true,
                Message = $"cache at '{cachePath}' looks corrupt but only {freeBytes} byte(s) are free "
                    + $"(need at least {_options.MinFreeDiskBytes}); not deleting it."
            };
        }

        _deleteDirectory(cachePath);
        await _ensureCacheInitializedAsync(cachePath, cancellationToken);

        var retryResult = await _runner.RunAsync(
            fetchArgs, cachePath, credentials, onProgressLine, cancellationToken);

        if (!retryResult.Success)
        {
            return new GitCacheFetchResult
            {
                Success = false,
                CachePath = cachePath,
                RecoveredFromCorruption = true,
                Message = "cache was corrupt, deleted and re-cloned once, but the re-clone also failed: "
                    + _firstLine(retryResult.StdErr, retryResult.Message)
            };
        }

        await _runner.RunAsync(new[] { "gc", "--auto" }, cachePath, null, null, cancellationToken);
        return new GitCacheFetchResult
        {
            Success = true,
            CachePath = cachePath,
            RecoveredFromCorruption = true,
            Message = $"cache at '{cachePath}' was corrupt; deleted and re-cloned successfully."
        };
    }

    /**
     * A fetch into a bare repo can fail for reasons that have nothing to do
     * with local corruption (wrong credentials, unreachable host). Running
     * <c>git fsck</c> tells the two apart: an intact repo that merely failed
     * to reach the remote passes fsck cleanly and must not be deleted.
     */
    private async Task<bool> _looksLikeCorruptionAsync(string cachePath, CancellationToken cancellationToken)
    {
        var fsckResult = await _runner.RunAsync(
            new[] { "fsck", "--no-progress" }, cachePath, null, null, cancellationToken);
        return !fsckResult.Success;
    }

    /** Known git wording for an unreadable/malformed pack - cheap enough to check on every fetch's stderr before paying for an fsck. */
    private static readonly string[] _corruptionMarkers =
    {
        "not a git packfile", "cannot be accessed", "bad object", "unable to read",
        "is corrupt", "SHA1 COLLISION"
    };

    private static bool _mentionsCorruption(string text) =>
        _corruptionMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private async Task _ensureCacheInitializedAsync(string cachePath, CancellationToken cancellationToken)
    {
        if (Directory.Exists(cachePath) && Directory.EnumerateFileSystemEntries(cachePath).Any())
        {
            return;
        }

        Directory.CreateDirectory(cachePath);
        var parent = Path.GetDirectoryName(cachePath) ?? cachePath;

        var initResult = await _runner.RunAsync(
            new[] { "init", "--bare", cachePath }, parent, null, null, cancellationToken);
        if (!initResult.Success)
        {
            throw new InvalidOperationException(
                $"git init --bare failed for cache '{cachePath}': {initResult.StdErr}");
        }

        await _runner.RunAsync(
            new[] { "config", "core.longpaths", "true" }, cachePath, null, null, cancellationToken);
    }

    private static string[] _buildFetchArgs(string sourceRemoteUrl, IReadOnlyList<string> fetchRefspecs)
    {
        var args = new List<string> { "fetch", "--prune", "--prune-tags", sourceRemoteUrl };
        args.AddRange(fetchRefspecs);
        return args.ToArray();
    }

    private async Task<_CacheLock> _acquireLockAsync(string cachePath, CancellationToken cancellationToken)
    {
        var lockPath = cachePath + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        var deadline = DateTime.UtcNow + _options.JobTimeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new _CacheLock(stream, cachePath, DateTimeOffset.UtcNow, _lockTrace);
            }
            catch (IOException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException(
                        $"Could not acquire the cache lock for '{cachePath}' within {_options.JobTimeout}.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
        }
    }

    /** Recursive delete that clears read-only attributes first - git marks pack and loose-object files read-only, which a plain recursive delete refuses on Windows. */
    private static void _deleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }

        Directory.Delete(path, recursive: true);
    }

    private static string _sanitizeSegment(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            sb.Append(c is '/' or '\\' || invalid.Contains(c) ? '_' : c);
        }

        return sb.ToString();
    }

    private static string _firstLine(string text, string? fallback)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        return string.IsNullOrEmpty(line) ? (fallback ?? "(no output)") : line;
    }

    private sealed class _CacheLock : IAsyncDisposable
    {
        private readonly FileStream _stream;
        private readonly string _cachePath;
        private readonly DateTimeOffset _acquiredAt;
        private readonly IGitCacheLockTrace _trace;

        public _CacheLock(FileStream stream, string cachePath, DateTimeOffset acquiredAt, IGitCacheLockTrace trace)
        {
            _stream = stream;
            _cachePath = cachePath;
            _acquiredAt = acquiredAt;
            _trace = trace;
        }

        public ValueTask DisposeAsync()
        {
            _stream.Dispose();
            _trace.Record(new GitCacheLockEvent(_cachePath, _acquiredAt, DateTimeOffset.UtcNow));
            return ValueTask.CompletedTask;
        }
    }
}
