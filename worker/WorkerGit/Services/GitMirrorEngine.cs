using System.Security.Cryptography;
using System.Text;
using Hannibal.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    /**
     * Per-rule override of the shrink and force-push guards (plan §5). Slice
     * 2 wires this from <c>Rule.AllowUnsafeRefChange</c>; the engine consumes
     * it starting this slice regardless of who sets it.
     */
    public bool AllowUnsafeRefChange { get; init; } = false;

    /**
     * Per-rule override of the adopt guard's "unmarked, non-empty
     * destination" case (plan §5). Never overrides a *mismatched* marker -
     * see <see cref="GitMirrorGuard.AdoptMismatch"/>. Slice 2 wires this from
     * <c>Rule.AllowAdopt</c>.
     */
    public bool AllowAdopt { get; init; } = false;

    /** Fires for stderr lines from fetch/push (git writes progress there). */
    public Action<string>? OnProgress { get; init; }
}

public enum GitMirrorOutcome
{
    Success,
    DoneWithErrors,
    Failure
}

/**
 * Which of §5's guards, if any, tripped and produced a
 * <see cref="GitMirrorOutcome.Failure"/>. <see cref="None"/> for every
 * non-guard outcome (including ordinary infrastructure failures such as an
 * unreachable remote) so callers can tell "a security guard refused this"
 * apart from "the network is down" (plan Gate E AC9).
 */
public enum GitMirrorGuard
{
    None,
    ZeroRef,
    SelfMirror,
    AdoptUnmarked,
    AdoptMismatch,
    Shrink,
    ForcePush
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

    /** <see cref="GitMirrorGuard.None"/> unless this is a guard-tripped <see cref="GitMirrorOutcome.Failure"/> (plan Gate E AC9). */
    public GitMirrorGuard TrippedGuard { get; init; } = GitMirrorGuard.None;
}

/**
 * The mirror operation itself (plan-git-repo-storage.md §3 + §4 + §5). Both
 * <see cref="GitMirrorOperation.Copy"/> and <see cref="GitMirrorOperation.Sync"/>
 * are wired; §5's guards (zero-ref, self-mirror, adopt, shrink, force-push)
 * run for both operations except where a guard is explicitly Sync-only
 * (shrink and force-push - Copy structurally cannot force or delete, so
 * those two attacks do not apply to it).
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

    /**
     * Where the adopt marker lives (plan §5 "Adopt guard"): deliberately
     * outside <see cref="MirroredNamespaces"/>, so §3's forced Sync push can
     * never delete it and §3's fetch (source-scoped, never touches the
     * destination's refs) can never import a spoofed one from a hostile
     * source. Identity lives in the ref NAME, not in the object it points at.
     */
    private const string MarkerNamespacePrefix = "refs/backer/mirror-of/";

    /**
     * Git's well-known empty-tree object id - valid in every repository
     * without ever being explicitly written (confirmed empirically:
     * <c>git cat-file -t 4b825dc642cb6eb9a060e54bf8d69288fbee4904</c> reports
     * "tree" even in a freshly initialised bare repo with zero objects).
     * <c>git commit-tree</c> against it, with the fixed author/committer
     * identity and date below, is what makes the marker commit's SHA a
     * deterministic constant - the plan asks for the marker's *identity* to
     * live entirely in the ref name (see <see cref="MarkerNamespacePrefix"/>),
     * so the object it points at is deliberately generic and carries no
     * information of its own.
     */
    private const string EmptyTreeSha = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    private const string MarkerCommitMessage =
        "Backer git-mirror adopt marker - do not delete. Identifies which source "
        + "this destination is a mirror of (plan-git-repo-storage.md §5).";

    private static readonly IReadOnlyDictionary<string, string> _markerCommitEnvironment = new Dictionary<string, string>
    {
        ["GIT_AUTHOR_NAME"] = "Backer",
        ["GIT_AUTHOR_EMAIL"] = "backer-mirror-marker@invalid",
        ["GIT_AUTHOR_DATE"] = "2000-01-01T00:00:00Z",
        ["GIT_COMMITTER_NAME"] = "Backer",
        ["GIT_COMMITTER_EMAIL"] = "backer-mirror-marker@invalid",
        ["GIT_COMMITTER_DATE"] = "2000-01-01T00:00:00Z"
    };

    private readonly GitWorkerOptions _options;
    private readonly GitCliRunner _runner;
    private readonly GitCacheManager _cacheManager;
    private readonly ILogger<GitMirrorEngine> _logger;

    public GitMirrorEngine(
        GitWorkerOptions options,
        GitCliRunner runner,
        GitCacheManager cacheManager,
        ILogger<GitMirrorEngine>? logger = null)
    {
        _options = options;
        _runner = runner;
        _cacheManager = cacheManager;
        _logger = logger ?? NullLogger<GitMirrorEngine>.Instance;
    }

    /**
     * Guard evaluation order (plan Gate E, all between the two ls-remote
     * probes and any push):
     *
     * 1. Source probe, then (a) ZERO-REF - unchanged from Gate D, still able
     *    to short-circuit before the destination is ever probed.
     * 2. Destination probe, then (b) SELF-MIRROR - deliberately BEFORE the
     *    §4 "already in sync" fast path: two endpoints that normalise to the
     *    same repository always compare ref-equal, so without this guard
     *    running first the fast path would silently report success instead
     *    of flagging the misconfiguration.
     * 3. §4's "already in sync" fast path (unchanged) - nothing left to push
     *    means nothing left to guard.
     * 4. (c) ADOPT - applies to both Copy and Sync (a push to an unclaimed,
     *    non-empty destination is unsafe regardless of forcing).
     * 5. (d) SHRINK - Sync only; Copy structurally never deletes.
     * 6. Fetch into the cache.
     * 7. (e) FORCE-PUSH - Sync only, and only possible once the source's
     *    objects are in the cache for <c>merge-base --is-ancestor</c>.
     * 8. The marker push (always, both operations - see
     *    <see cref="_pushMarkerAsync"/>), then the data push.
     */
    public async Task<GitMirrorResult> ExecuteAsync(GitMirrorRequest request, CancellationToken cancellationToken)
    {
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

        var srcProbe = await _lsRemoteAsync(request.Source, MirroredNamespaces, cancellationToken);
        if (!srcProbe.Success)
        {
            return _failure($"could not list refs on the source: {srcProbe.Error}");
        }

        if (srcProbe.Refs.Count == 0)
        {
            // Zero-ref guard (plan §5): a source with nothing in MIRRORED
            // must never look like "already in sync" and must never be
            // pushed from - regardless of what the destination looks like.
            return _guardFailure(
                GitMirrorGuard.ZeroRef, GitGuardEvents.ZeroRef,
                "source resolves to zero refs in the mirrored namespaces "
                + "(refs/heads/*, refs/tags/*, refs/notes/*); refusing to push.");
        }

        var dstProbe = await _lsRemoteAsync(request.Destination, MirroredNamespaces, cancellationToken);
        if (!dstProbe.Success)
        {
            return _failure($"could not list refs on the destination: {dstProbe.Error}");
        }

        var normalizedSourceUrl = GitRemoteUrl.Normalize(request.Source.RemoteUrl, null);
        var normalizedDestinationUrl = GitRemoteUrl.Normalize(request.Destination.RemoteUrl, null);
        if (string.Equals(normalizedSourceUrl, normalizedDestinationUrl, StringComparison.Ordinal))
        {
            // Self-mirror guard (plan §5 last bullet) - the run-time belt.
            // Rule creation already rejects this (Gate B), but that check is
            // storage-id-scoped and blind to two Storage rows pointing at the
            // same host and account; this re-check is not. Deliberately
            // before the equality fast path below - without that ordering a
            // self-mirroring rule would always compare ref-equal to itself
            // and silently report "already in sync" forever.
            return _guardFailure(
                GitMirrorGuard.SelfMirror, GitGuardEvents.SelfMirror,
                $"source and destination normalise to the same repository ('{normalizedDestinationUrl}'); "
                + "refusing to push (run-time self-mirror guard).");
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

        var markerRefName = MarkerNamespacePrefix
            + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedSourceUrl))).ToLowerInvariant();

        var markerProbe = await _lsRemoteAsync(request.Destination, new[] { MarkerNamespacePrefix + "*" }, cancellationToken);
        if (!markerProbe.Success)
        {
            return _failure($"could not list the adopt marker on the destination: {markerProbe.Error}");
        }

        var mismatchedMarkers = markerProbe.Refs.Keys
            .Where(refName => !string.Equals(refName, markerRefName, StringComparison.Ordinal))
            .ToArray();
        if (mismatchedMarkers.Length > 0)
        {
            // Adopt guard, mismatch branch (plan §5 / Gate E AC7): a
            // destination already marked as mirror-of-X refuses a push from Y
            // unconditionally - AllowAdopt only ever covers an *unmarked*
            // destination (below), never a re-point away from an existing
            // identity. Named with both hashes, per the brief.
            return _guardFailure(
                GitMirrorGuard.AdoptMismatch, GitGuardEvents.AdoptMismatch,
                $"destination already carries a Backer mirror marker for a different source "
                + $"({string.Join(", ", mismatchedMarkers)}); this push's source hashes to '{markerRefName}'. "
                + "A destination can only ever be adopted from one source identity; refusing to push.");
        }

        var hasMatchingMarker = markerProbe.Refs.ContainsKey(markerRefName);
        var destinationHasMirroredRefs = dstProbe.Refs.Count > 0;
        if (destinationHasMirroredRefs && !hasMatchingMarker && !request.AllowAdopt)
        {
            // Adopt guard, unmarked branch: a non-empty destination with no
            // marker at all (never adopted by Backer) refuses a first push
            // unless the rule explicitly opts in.
            return _guardFailure(
                GitMirrorGuard.AdoptUnmarked, GitGuardEvents.AdoptUnmarked,
                $"destination already contains {dstProbe.Refs.Count} ref(s) in the mirrored namespaces but "
                + $"carries no Backer mirror marker ('{markerRefName}'); refusing to adopt an unmarked "
                + "destination. Set AllowAdopt on the rule to override.");
        }

        IReadOnlyList<string> deletions = Array.Empty<string>();
        if (request.Operation == GitMirrorOperation.Sync)
        {
            // Shrink guard (Sync only - Copy structurally never deletes, so
            // it cannot destroy anything this guard protects against).
            var missingFromSource = dstProbe.Refs.Keys.Except(srcProbe.Refs.Keys).ToArray();
            if (missingFromSource.Length > 0)
            {
                var exceedsShrinkGuard =
                    missingFromSource.Length * 100 > _options.MaxRefShrinkPercent * dstProbe.Refs.Count;
                if (exceedsShrinkGuard && !request.AllowUnsafeRefChange)
                {
                    return _guardFailure(
                        GitMirrorGuard.Shrink, GitGuardEvents.Shrink,
                        $"{missingFromSource.Length} of {dstProbe.Refs.Count} destination ref(s) are absent "
                        + $"from the source, exceeding the MaxRefShrinkPercent guard "
                        + $"({_options.MaxRefShrinkPercent}%): {string.Join(", ", missingFromSource)}. "
                        + "Set AllowUnsafeRefChange on the rule to override.");
                }

                deletions = missingFromSource;
            }
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

        if (request.Operation == GitMirrorOperation.Sync)
        {
            // Force-push guard (Sync only; under Copy a rejected non-fast-
            // forward ref is already reported via the post-push re-probe
            // below, not forced in the first place). Only possible now that
            // the source's objects are in the cache.
            var changedRefs = new List<string>();
            var nonFfRefs = new List<string>();
            foreach (var (refName, sourceSha) in srcProbe.Refs)
            {
                if (!dstProbe.Refs.TryGetValue(refName, out var destinationSha)
                    || string.Equals(sourceSha, destinationSha, StringComparison.Ordinal))
                {
                    continue;
                }

                changedRefs.Add(refName);
                if (!await _isFastForwardAsync(fetchResult.CachePath, destinationSha, sourceSha, cancellationToken))
                {
                    nonFfRefs.Add(refName);
                }
            }

            if (nonFfRefs.Count > 0)
            {
                var exceedsForcePushGuard = nonFfRefs.Count * 100 > _options.MaxNonFfPercent * changedRefs.Count;
                if (exceedsForcePushGuard && !request.AllowUnsafeRefChange)
                {
                    return _guardFailure(
                        GitMirrorGuard.ForcePush, GitGuardEvents.ForcePush,
                        $"{nonFfRefs.Count} of {changedRefs.Count} changed ref(s) are not fast-forward from "
                        + $"the destination, exceeding the MaxNonFfPercent guard ({_options.MaxNonFfPercent}%): "
                        + $"{string.Join(", ", nonFfRefs)}. Set AllowUnsafeRefChange on the rule to override.");
                }
            }
        }

        // Adopt guard's write side (plan §5 / Gate E AC7): the marker is
        // pushed as its OWN, separate `git push` before any data push, so a
        // crash between the two can never leave data on the destination
        // without the marker that identifies it. Unconditional (even when
        // hasMatchingMarker is already true) - re-pushing the same, fixed
        // commit SHA is a harmless no-op and keeps the marker's presence a
        // simple invariant rather than a conditional one.
        var destinationCredentials = new GitCredentials
        {
            Username = request.Destination.Username, Token = request.Destination.Token
        };

        var markerPushResult = await _pushMarkerAsync(
            fetchResult.CachePath, request.Destination.RemoteUrl, markerRefName, destinationCredentials,
            request.OnProgress, cancellationToken);
        if (!markerPushResult.Success)
        {
            return _failure(
                "could not push the adopt marker to the destination: "
                + (_firstLine(markerPushResult.StdErr) ?? markerPushResult.Message ?? "push failed"));
        }

        if (deletions.Count > 0)
        {
            // Bounded, logged (plan §3): deletions are always this explicit,
            // computed list - never an implicit consequence of `--mirror`.
            _logger.LogInformation(
                "Sync: deleting {Count} destination ref(s) absent from the source: {Refs}",
                deletions.Count, string.Join(", ", deletions));
        }

        // Sync semantics (plan §3): forced refspecs for every MIRRORED
        // namespace, plus explicit deletions for what §4/§5's shrink guard
        // already vetted. Copy keeps Gate D's unforced, non-deleting
        // refspecs unchanged. Either way, atomic push first with a
        // non-atomic retry - covers "the destination does not support
        // --atomic" and "some refs were rejected", and the re-probe below is
        // what actually decides the outcome.
        var pushRefspecs = request.Operation == GitMirrorOperation.Sync
            ? MirroredNamespaces.Select(ns => $"+{ns}:{ns}").Concat(deletions.Select(refName => $":{refName}")).ToArray()
            : MirroredNamespaces.Select(ns => $"{ns}:{ns}").ToArray();

        var pushResult = await _pushAsync(
            fetchResult.CachePath, request.Destination.RemoteUrl, pushRefspecs, destinationCredentials,
            withAtomic: true, request.OnProgress, cancellationToken);
        if (!pushResult.Success)
        {
            pushResult = await _pushAsync(
                fetchResult.CachePath, request.Destination.RemoteUrl, pushRefspecs, destinationCredentials,
                withAtomic: false, request.OnProgress, cancellationToken);
        }

        var postPushProbe = await _lsRemoteAsync(request.Destination, MirroredNamespaces, cancellationToken);
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
        // --progress for the same reason as the fetch side (Gate D AC15):
        // a redirected, non-tty pipe otherwise gets no progress lines at all.
        var args = new List<string> { "push", "--progress" };
        if (withAtomic)
        {
            args.Add("--atomic");
        }

        args.Add(destinationUrl);
        args.AddRange(refspecs);

        return await _runner.RunAsync(args, cachePath, credentials, onProgress, cancellationToken);
    }

    /**
     * Writes the marker commit into the cache (deterministic - see
     * <see cref="EmptyTreeSha"/>) and pushes it to the destination by SHA,
     * with no local ref needed on either side. Never forced: the object is
     * fixed, so every push either creates the ref for the first time or
     * updates it to the exact SHA it already has - never a rewrite.
     */
    private async Task<GitRunResult> _pushMarkerAsync(
        string cachePath, string destinationUrl, string markerRefName, GitCredentials credentials,
        Action<string>? onProgress, CancellationToken cancellationToken)
    {
        var commitTreeResult = await _runner.RunAsync(
            new[] { "commit-tree", EmptyTreeSha, "-m", MarkerCommitMessage }, cachePath, null, null,
            cancellationToken, _markerCommitEnvironment);
        if (!commitTreeResult.Success)
        {
            return commitTreeResult;
        }

        var markerCommitSha = commitTreeResult.StdOut.Trim();
        var refspec = $"{markerCommitSha}:{markerRefName}";
        return await _pushAsync(
            cachePath, destinationUrl, new[] { refspec }, credentials, withAtomic: false, onProgress,
            cancellationToken);
    }

    /**
     * Force-push guard classification (plan §5): is <paramref name="destinationSha"/>
     * an ancestor of <paramref name="sourceSha"/> in the cache? The cache only
     * ever fetches from the SOURCE - the destination's objects are never
     * pulled in - so a destination SHA with no shared history (a fresh adopt,
     * or a genuinely rewritten branch) will not even be present locally.
     * <c>git merge-base --is-ancestor</c> fails for a missing object exactly
     * as it does for "not an ancestor", and both are treated identically
     * here: unprovable ancestry is not fast-forward, the safe failure
     * direction for a guard that exists specifically to catch history rewrites.
     */
    private async Task<bool> _isFastForwardAsync(
        string cachePath, string destinationSha, string sourceSha, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            new[] { "merge-base", "--is-ancestor", destinationSha, sourceSha }, cachePath, null, null,
            cancellationToken);
        return result.Success;
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

    private async Task<_LsRemoteProbe> _lsRemoteAsync(
        GitMirrorEndpoint endpoint, IReadOnlyList<string> patterns, CancellationToken cancellationToken)
    {
        var args = new List<string> { "ls-remote", endpoint.RemoteUrl };
        args.AddRange(patterns);

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

    /** A guard-tripped failure (plan Gate E AC9): logs at Warning under the guard's own EventId and carries the guard on the result. */
    private GitMirrorResult _guardFailure(GitMirrorGuard guard, EventId eventId, string message)
    {
        _logger.LogWarning(eventId, "Git mirror guard tripped ({Guard}): {Message}", guard, message);
        return new GitMirrorResult
        {
            Outcome = GitMirrorOutcome.Failure,
            Message = message,
            TransferPerformed = false,
            TrippedGuard = guard
        };
    }

    private sealed record _LsRemoteProbe(bool Success, IReadOnlyDictionary<string, string> Refs, string? Error);
}
