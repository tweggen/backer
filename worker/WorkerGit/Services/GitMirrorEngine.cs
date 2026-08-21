using WorkerGit.Configuration;

namespace WorkerGit.Services;

/** Mirrors <see cref="GitMirrorRequest.Operation"/> - kept separate from
 * <c>Hannibal.Models.Rule.RuleOperation</c> so this project never has to
 * reference the domain model just to describe what it can execute. */
public enum GitMirrorOperation
{
    Nop,
    Copy,
    Sync
}

/** One side of a mirror: a remote URL (no userinfo - plan §6) plus the
 * credentials the askpass helper hands to git. */
public sealed class GitMirrorEndpoint
{
    public required string RemoteUrl { get; init; }

    public string Username { get; init; } = string.Empty;

    public string Token { get; init; } = string.Empty;
}

public sealed class GitMirrorRequest
{
    public required GitMirrorEndpoint Source { get; init; }

    public required GitMirrorEndpoint Destination { get; init; }

    public required GitMirrorOperation Operation { get; init; }

    public required string UserId { get; init; }

    /** Storage.UriSchema of the source - part of the cache path (plan §7). */
    public required string SourceUriSchema { get; init; }

    /** Fires for stderr lines from fetch/push (git writes progress there). */
    public Action<string>? OnProgress { get; init; }
}

public enum GitMirrorOutcome
{
    Success,
    DoneWithErrors,
    Failure
}

/** One MIRRORED ref that did not end up matching on the destination after the push. */
public sealed record GitRefError(string RefName, string Reason);

public sealed class GitMirrorResult
{
    public required GitMirrorOutcome Outcome { get; init; }

    public required string Message { get; init; }

    public IReadOnlyList<GitRefError> RefErrors { get; init; } = Array.Empty<GitRefError>();

    /** False for Nop and for the already-in-sync fast path - lets tests assert "no transfer happened" without parsing the message. */
    public bool TransferPerformed { get; init; }
}

/**
 * The mirror operation itself (plan-git-repo-storage.md §3 + §4). Gate D
 * wires only <see cref="GitMirrorOperation.Copy"/> - §5's guards (shrink,
 * force-push, adopt, self-mirror) are Gate E; the zero-ref guard is
 * implemented here because Copy needs it too (an empty source must never
 * look like "nothing to do").
 */
public sealed class GitMirrorEngine
{
    /**
     * The fixed, closed ref namespace set this engine will ever touch on
     * either side (plan §3). Never <c>refs/*</c>, never <c>--mirror</c> - a
     * real GitHub source has thousands of refs outside this set
     * (<c>refs/pull/*</c>) that the destination forge refuses to accept.
     */
    public static readonly IReadOnlyList<string> MirroredNamespaces =
        new[] { "refs/heads/*", "refs/tags/*", "refs/notes/*" };

    private readonly GitWorkerOptions _options;
    private readonly GitCliRunner _runner;
    private readonly GitCacheManager _cacheManager;

    public GitMirrorEngine(GitWorkerOptions options, GitCliRunner runner, GitCacheManager cacheManager)
    {
        _options = options;
        _runner = runner;
        _cacheManager = cacheManager;
    }

    public async Task<GitMirrorResult> ExecuteAsync(GitMirrorRequest request, CancellationToken cancellationToken)
    {
        if (request.Operation == GitMirrorOperation.Sync)
        {
            // Gate E ships Sync (plan §5's guards make it safe to force/delete).
            throw new NotSupportedException(
                "GitMirrorOperation.Sync is not implemented until Gate E ships the safety guards (plan §5).");
        }

        if (request.Operation == GitMirrorOperation.Nop)
        {
            // The bulk operation switch (Api/Program.cs:502-512) can force Nop
            // onto any job; it must complete without touching either remote,
            // never by fabricating a fake success through some default path.
            return new GitMirrorResult
            {
                Outcome = GitMirrorOutcome.Success,
                Message = "Nop: completed without contacting either remote.",
                TransferPerformed = false
            };
        }

        Directory.CreateDirectory(_options.CacheRoot ?? throw new InvalidOperationException(
            "GitWorkerOptions.CacheRoot must be set."));

        var srcProbe = await _lsRemoteAsync(request.Source, cancellationToken);
        if (!srcProbe.Success)
        {
            return _failure($"could not list refs on the source: {srcProbe.Error}");
        }

        if (srcProbe.Refs.Count == 0)
        {
            // Zero-ref guard (plan §5, in scope for Gate D because Copy needs
            // it too): a source with nothing in MIRRORED must never look like
            // "already in sync" and must never be pushed from.
            return _failure(
                "source resolves to zero refs in the mirrored namespaces "
                + "(refs/heads/*, refs/tags/*, refs/notes/*); refusing to push.");
        }

        var dstProbe = await _lsRemoteAsync(request.Destination, cancellationToken);
        if (!dstProbe.Success)
        {
            return _failure($"could not list refs on the destination: {dstProbe.Error}");
        }

        if (_refsEqual(srcProbe.Refs, dstProbe.Refs))
        {
            // The destination is the baseline (plan §4): two ls-remote calls
            // and nothing else. No fetch, no push, no persisted state.
            return new GitMirrorResult
            {
                Outcome = GitMirrorOutcome.Success,
                Message = "already in sync",
                TransferPerformed = false
            };
        }

        var fetchRefspecs = MirroredNamespaces.Select(ns => $"+{ns}:{ns}").ToArray();
        var fetchResult = await _cacheManager.FetchAsync(
            request.UserId, request.SourceUriSchema, request.Source.RemoteUrl, request.Source.RemoteUrl,
            new GitCredentials { Username = request.Source.Username, Token = request.Source.Token },
            fetchRefspecs, request.OnProgress, cancellationToken);

        if (!fetchResult.Success)
        {
            return _failure(fetchResult.Message ?? "fetch into the mirror cache failed");
        }

        var lfsBranches = await _detectLfsAsync(srcProbe.Refs, fetchResult.CachePath, cancellationToken);

        // Copy semantics (plan §5): no leading '+', no deletions. Requesting
        // --atomic and retrying once without it covers both "the destination
        // does not support --atomic" and "some refs were rejected" - either
        // way the retry gives every fast-forwardable ref its best chance to
        // land, and the re-probe below is what actually decides the outcome.
        var pushRefspecs = MirroredNamespaces.Select(ns => $"{ns}:{ns}").ToArray();
        var pushCredentials = new GitCredentials
        {
            Username = request.Destination.Username, Token = request.Destination.Token
        };

        var pushResult = await _pushAsync(
            fetchResult.CachePath, request.Destination.RemoteUrl, pushRefspecs, pushCredentials,
            withAtomic: true, request.OnProgress, cancellationToken);
        if (!pushResult.Success)
        {
            pushResult = await _pushAsync(
                fetchResult.CachePath, request.Destination.RemoteUrl, pushRefspecs, pushCredentials,
                withAtomic: false, request.OnProgress, cancellationToken);
        }

        var postPushProbe = await _lsRemoteAsync(request.Destination, cancellationToken);
        var refErrors = postPushProbe.Success
            ? _computeRefErrors(srcProbe.Refs, postPushProbe.Refs)
            : srcProbe.Refs.Keys
                .Select(refName => new GitRefError(refName, "could not re-probe the destination after the push"))
                .ToArray();

        return _buildResult(refErrors, lfsBranches, srcProbe.Refs.Count);
    }

    private static GitMirrorResult _buildResult(
        IReadOnlyList<GitRefError> refErrors, IReadOnlyList<string> lfsBranches, int mirroredRefCount)
    {
        if (refErrors.Count > 0)
        {
            var refMessage = string.Join("; ", refErrors.Select(e => $"{e.RefName} ({e.Reason})"));
            var message = lfsBranches.Count > 0
                ? $"push completed with errors: {refMessage}. Also, git-LFS is not transferred by this "
                  + $"engine and was detected on: {string.Join(", ", lfsBranches)}."
                : $"push completed with errors: {refMessage}";

            return new GitMirrorResult
            {
                Outcome = GitMirrorOutcome.DoneWithErrors,
                Message = message,
                RefErrors = refErrors,
                TransferPerformed = true
            };
        }

        if (lfsBranches.Count > 0)
        {
            return new GitMirrorResult
            {
                Outcome = GitMirrorOutcome.DoneWithErrors,
                Message = "mirrored successfully, but git-LFS objects are not transferred by this engine; "
                    + $"filter=lfs was found in .gitattributes on: {string.Join(", ", lfsBranches)}.",
                TransferPerformed = true
            };
        }

        return new GitMirrorResult
        {
            Outcome = GitMirrorOutcome.Success,
            Message = $"mirrored {mirroredRefCount} ref(s).",
            TransferPerformed = true
        };
    }

    private async Task<GitRunResult> _pushAsync(
        string cachePath, string destinationUrl, IReadOnlyList<string> refspecs, GitCredentials credentials,
        bool withAtomic, Action<string>? onProgress, CancellationToken cancellationToken)
    {
        var args = new List<string> { "push" };
        if (withAtomic)
        {
            args.Add("--atomic");
        }

        args.Add(destinationUrl);
        args.AddRange(refspecs);

        return await _runner.RunAsync(args, cachePath, credentials, onProgress, cancellationToken);
    }

    /**
     * Reads <c>.gitattributes</c> at every fetched <c>refs/heads/*</c> tip
     * without a checkout (plan §8 / AC12): <c>git cat-file -p &lt;sha&gt;:.gitattributes</c>
     * in the bare cache. A missing file (non-zero exit) is not an error - most
     * repositories have none.
     */
    private async Task<IReadOnlyList<string>> _detectLfsAsync(
        IReadOnlyDictionary<string, string> sourceRefs, string cachePath, CancellationToken cancellationToken)
    {
        var branches = new List<string>();
        foreach (var (refName, sha) in sourceRefs)
        {
            if (!refName.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                continue;
            }

            var result = await _runner.RunAsync(
                new[] { "cat-file", "-p", $"{sha}:.gitattributes" }, cachePath, null, null, cancellationToken);
            if (result.Success && result.StdOut.Contains("filter=lfs", StringComparison.Ordinal))
            {
                branches.Add(refName);
            }
        }

        return branches;
    }

    private static IReadOnlyList<GitRefError> _computeRefErrors(
        IReadOnlyDictionary<string, string> sourceRefs, IReadOnlyDictionary<string, string> destinationRefs)
    {
        var errors = new List<GitRefError>();
        foreach (var (refName, sourceSha) in sourceRefs)
        {
            if (!destinationRefs.TryGetValue(refName, out var destinationSha))
            {
                errors.Add(new GitRefError(refName, "not present on the destination after the push"));
            }
            else if (!string.Equals(sourceSha, destinationSha, StringComparison.Ordinal))
            {
                errors.Add(new GitRefError(
                    refName, $"destination is at {destinationSha}, expected {sourceSha} "
                        + "(rejected as non-fast-forward, or the push failed)"));
            }
        }

        return errors;
    }

    private async Task<_LsRemoteProbe> _lsRemoteAsync(GitMirrorEndpoint endpoint, CancellationToken cancellationToken)
    {
        var args = new List<string> { "ls-remote", endpoint.RemoteUrl };
        args.AddRange(MirroredNamespaces);

        var credentials = new GitCredentials { Username = endpoint.Username, Token = endpoint.Token };
        var result = await _runner.RunAsync(args, _options.CacheRoot!, credentials, null, cancellationToken);

        if (!result.Success)
        {
            return new _LsRemoteProbe(
                false, new Dictionary<string, string>(),
                _firstLine(result.StdErr) ?? result.Message ?? "ls-remote failed");
        }

        return new _LsRemoteProbe(true, _parseLsRemote(result.StdOut), null);
    }

    private static Dictionary<string, string> _parseLsRemote(string stdout)
    {
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var tabIndex = line.IndexOf('\t');
            if (tabIndex < 0)
            {
                continue;
            }

            refs[line[(tabIndex + 1)..]] = line[..tabIndex];
        }

        return refs;
    }

    private static bool _refsEqual(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (var (refName, sha) in a)
        {
            if (!b.TryGetValue(refName, out var otherSha) || !string.Equals(sha, otherSha, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string? _firstLine(string text)
    {
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
    }

    private static GitMirrorResult _failure(string message) =>
        new() { Outcome = GitMirrorOutcome.Failure, Message = message, TransferPerformed = false };

    private sealed record _LsRemoteProbe(bool Success, IReadOnlyDictionary<string, string> Refs, string? Error);
}
