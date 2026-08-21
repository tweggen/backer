using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Hannibal.Models;
using Microsoft.Extensions.Logging;
using WorkerGit.Services;
using WorkerGit.Tests.TestSupport;
using Xunit;

namespace WorkerGit.Tests;

/**
 * Gate E's safety-guard suite and Sync semantics (plan-git-repo-storage.md
 * §5, Gate E AC1-7 and AC9 - AC8 and AC10 are slice 2), against real bare
 * repositories in a temp directory - no network, no account. Every
 * destructive scenario asserts the destination's <c>git for-each-ref</c>
 * output is byte-identical before/after a tripped guard
 * (<see cref="GitTestRepo.ForEachRefRaw"/>), not merely that the job failed.
 */
public sealed class GitMirrorEngineGuardTests : IDisposable
{
    private readonly GitTestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    /** AC1: Sync deletes a destination branch after it is deleted at the source; Copy on the same fixture does not. */
    [Fact]
    public async Task AC1_SyncDeletesDestinationBranchDeletedAtSource_CopyDoesNot()
    {
        var sourceBare = _initSourceWithBranches("source", 3); // b0, b1, b2

        var destinationBareSync = _env.NewPath("dest-sync.git");
        GitTestRepo.InitBare(destinationBareSync);
        var destinationBareCopy = _env.NewPath("dest-copy.git");
        GitTestRepo.InitBare(destinationBareCopy);

        foreach (var destination in new[] { destinationBareSync, destinationBareCopy })
        {
            var (setupEngine, _, _) = _buildGuardedEngine();
            var setupResult = await setupEngine.ExecuteAsync(
                _request(sourceBare, destination, GitMirrorOperation.Copy), CancellationToken.None);
            setupResult.Outcome.Should().Be(GitMirrorOutcome.Success);
        }

        GitTestRepo.DeleteRef(sourceBare, "refs/heads/b2"); // 1 of 3 -> well under the default 50% shrink ceiling

        var (syncEngine, _, _) = _buildGuardedEngine();
        var syncResult = await syncEngine.ExecuteAsync(
            _request(sourceBare, destinationBareSync, GitMirrorOperation.Sync), CancellationToken.None);
        syncResult.Outcome.Should().Be(GitMirrorOutcome.Success, syncResult.Message);
        GitTestRepo.ForEachRef(destinationBareSync, "refs/heads/*").Should().NotContainKey("refs/heads/b2");

        var (copyEngine, _, _) = _buildGuardedEngine();
        var copyResult = await copyEngine.ExecuteAsync(
            _request(sourceBare, destinationBareCopy, GitMirrorOperation.Copy), CancellationToken.None);
        copyResult.Outcome.Should().Be(GitMirrorOutcome.Success, copyResult.Message);
        GitTestRepo.ForEachRef(destinationBareCopy, "refs/heads/*").Should().ContainKey("refs/heads/b2");
    }

    /** AC2: Sync leaves a foreign-namespace ref, and the adopt marker, untouched on the destination. */
    [Fact]
    public async Task AC2_SyncLeavesForeignNamespaceRefsAndTheMarkerUntouched()
    {
        var sourceBare = _initSourceWithBranches("source", 2); // b0, b1
        var destinationBare = _env.NewPath("dest.git");
        GitTestRepo.InitBare(destinationBare);

        var (setupEngine, _, _) = _buildGuardedEngine();
        var setupResult = await setupEngine.ExecuteAsync(
            _request(sourceBare, destinationBare, GitMirrorOperation.Copy), CancellationToken.None);
        setupResult.Outcome.Should().Be(GitMirrorOutcome.Success);

        var markerRefName = GitTestRepo.ForEachRef(destinationBare, "refs/backer/mirror-of/*").Keys.Single();

        var b0Sha = GitTestRepo.ForEachRef(sourceBare, "refs/heads/b0")["refs/heads/b0"];
        GitTestRepo.UpdateRef(destinationBare, "refs/pull/1/head", b0Sha);
        GitTestRepo.UpdateRef(destinationBare, "refs/custom/x", b0Sha);

        // Give the Sync real work to do (a deletion), not just the already-in-sync fast path.
        GitTestRepo.DeleteRef(sourceBare, "refs/heads/b1");

        var (syncEngine, _, _) = _buildGuardedEngine();
        var syncResult = await syncEngine.ExecuteAsync(
            _request(sourceBare, destinationBare, GitMirrorOperation.Sync), CancellationToken.None);
        syncResult.Outcome.Should().Be(GitMirrorOutcome.Success, syncResult.Message);

        GitTestRepo.ForEachRef(destinationBare, "refs/pull/1/head").Should().ContainKey("refs/pull/1/head");
        GitTestRepo.ForEachRef(destinationBare, "refs/custom/x").Should().ContainKey("refs/custom/x");
        GitTestRepo.ForEachRef(destinationBare, "refs/backer/mirror-of/*").Should().ContainKey(markerRefName);
        GitTestRepo.ForEachRef(destinationBare, "refs/heads/*").Should().NotContainKey("refs/heads/b1");
    }

    /** AC3: zero-ref guard - empty source, both operations, destination byte-identical before/after. */
    [Theory]
    [InlineData(GitMirrorOperation.Copy)]
    [InlineData(GitMirrorOperation.Sync)]
    public async Task AC3_ZeroRefGuardFailsAndLeavesDestinationByteIdentical(GitMirrorOperation operation)
    {
        var emptySourceBare = _env.NewPath("empty-source.git");
        GitTestRepo.InitBare(emptySourceBare);

        var destinationBare = _env.NewPath("dest.git");
        GitTestRepo.InitBare(destinationBare);

        // Seed the destination via a real (unrelated) mirror first, so
        // "byte-identical before/after" is a meaningful assertion rather than
        // a comparison of two empty repos.
        var seedSourceBare = _initSourceWithBranches("seed", 2);
        var (seedEngine, _, _) = _buildGuardedEngine();
        var seedResult = await seedEngine.ExecuteAsync(
            _request(seedSourceBare, destinationBare, GitMirrorOperation.Copy), CancellationToken.None);
        seedResult.Outcome.Should().Be(GitMirrorOutcome.Success);

        var beforeSnapshot = GitTestRepo.ForEachRefRaw(destinationBare);

        var (engine, _, logger) = _buildGuardedEngine();
        var result = await engine.ExecuteAsync(
            _request(emptySourceBare, destinationBare, operation), CancellationToken.None);

        result.Outcome.Should().Be(GitMirrorOutcome.Failure);
        result.TrippedGuard.Should().Be(GitMirrorGuard.ZeroRef);
        result.TransferPerformed.Should().BeFalse();
        GitTestRepo.ForEachRefRaw(destinationBare).Should().Be(beforeSnapshot);
        logger.Entries.Should().Contain(e => e.EventId == GitGuardEvents.ZeroRef && e.Level == LogLevel.Warning);
    }

    /** AC4: shrink guard - destination 10 MIRRORED refs, source shrunk to 2. */
    [Fact]
    public async Task AC4_ShrinkGuardBlocksLargeDeletionUnlessOverridden()
    {
        var sourceBare = _initSourceWithBranches("source", 10);
        var destinationBare = _env.NewPath("dest.git");
        GitTestRepo.InitBare(destinationBare);

        var (setupEngine, _, _) = _buildGuardedEngine();
        var setupResult = await setupEngine.ExecuteAsync(
            _request(sourceBare, destinationBare, GitMirrorOperation.Copy), CancellationToken.None);
        setupResult.Outcome.Should().Be(GitMirrorOutcome.Success);
        GitTestRepo.ForEachRef(destinationBare, "refs/heads/*").Should().HaveCount(10);

        var removedBranches = new List<string>();
        for (var i = 2; i < 10; i++)
        {
            GitTestRepo.DeleteRef(sourceBare, $"refs/heads/b{i}");
            removedBranches.Add($"refs/heads/b{i}");
        }

        GitTestRepo.ForEachRef(sourceBare, "refs/heads/*").Should().HaveCount(2);

        var beforeSnapshot = GitTestRepo.ForEachRefRaw(destinationBare);

        var (failEngine, _, failLogger) = _buildGuardedEngine();
        var failResult = await failEngine.ExecuteAsync(
            _request(sourceBare, destinationBare, GitMirrorOperation.Sync), CancellationToken.None);

        failResult.Outcome.Should().Be(GitMirrorOutcome.Failure);
        failResult.TrippedGuard.Should().Be(GitMirrorGuard.Shrink);
        foreach (var refName in removedBranches)
        {
            failResult.Message.Should().Contain(refName);
        }

        GitTestRepo.ForEachRefRaw(destinationBare).Should().Be(beforeSnapshot);
        failLogger.Entries.Should().Contain(e => e.EventId == GitGuardEvents.Shrink && e.Level == LogLevel.Warning);

        var (overrideEngine, _, _) = _buildGuardedEngine();
        var overrideResult = await overrideEngine.ExecuteAsync(
            _request(sourceBare, destinationBare, GitMirrorOperation.Sync, allowUnsafeRefChange: true),
            CancellationToken.None);

        overrideResult.Outcome.Should().Be(GitMirrorOutcome.Success, overrideResult.Message);
        GitTestRepo.ForEachRef(destinationBare, "refs/heads/*").Should().HaveCount(2);
    }

    /** AC5: force-push guard - source rewrites unrelated history over every branch, ref count unchanged. */
    [Fact]
    public async Task AC5_ForcePushGuardBlocksRewrittenHistoryUnlessOverridden()
    {
        var sourceBare = _initSourceWithBranches("source", 2); // b0, b1
        var destinationBare = _env.NewPath("dest.git");
        GitTestRepo.InitBare(destinationBare);

        var (setupEngine, _, _) = _buildGuardedEngine();
        var setupResult = await setupEngine.ExecuteAsync(
            _request(sourceBare, destinationBare, GitMirrorOperation.Copy), CancellationToken.None);
        setupResult.Outcome.Should().Be(GitMirrorOutcome.Success);

        _forcePushUnrelatedCommit(sourceBare, "b0", "rewritten-b0");
        _forcePushUnrelatedCommit(sourceBare, "b1", "rewritten-b1");
        GitTestRepo.ForEachRef(sourceBare, "refs/heads/*").Should().HaveCount(2); // same ref count - the attack the guard exists for

        var beforeSnapshot = GitTestRepo.ForEachRefRaw(destinationBare);

        var (failEngine, _, failLogger) = _buildGuardedEngine();
        var failResult = await failEngine.ExecuteAsync(
            _request(sourceBare, destinationBare, GitMirrorOperation.Sync), CancellationToken.None);

        failResult.Outcome.Should().Be(GitMirrorOutcome.Failure);
        failResult.TrippedGuard.Should().Be(GitMirrorGuard.ForcePush);
        failResult.Message.Should().Contain("refs/heads/b0");
        failResult.Message.Should().Contain("refs/heads/b1");
        GitTestRepo.ForEachRefRaw(destinationBare).Should().Be(beforeSnapshot);
        failLogger.Entries.Should().Contain(e => e.EventId == GitGuardEvents.ForcePush && e.Level == LogLevel.Warning);

        var (overrideEngine, _, _) = _buildGuardedEngine();
        var overrideResult = await overrideEngine.ExecuteAsync(
            _request(sourceBare, destinationBare, GitMirrorOperation.Sync, allowUnsafeRefChange: true),
            CancellationToken.None);

        overrideResult.Outcome.Should().Be(GitMirrorOutcome.Success, overrideResult.Message);
        GitTestRepo.ForEachRef(destinationBare, "refs/heads/*")
            .Should().BeEquivalentTo(GitTestRepo.ForEachRef(sourceBare, "refs/heads/*"));
    }

    /** AC6: non-fast-forward under Copy - destination branch unchanged, DoneWithErrors naming the ref. */
    [Fact]
    public async Task AC6_NonFastForwardUnderCopyReportsDoneWithErrorsAndLeavesTheRefUnchanged()
    {
        var sourceBare = _initSourceWithBranches("source", 2); // b0, b1
        var destinationBare = _env.NewPath("dest.git");
        GitTestRepo.InitBare(destinationBare);

        var (setupEngine, _, _) = _buildGuardedEngine();
        var setupResult = await setupEngine.ExecuteAsync(
            _request(sourceBare, destinationBare, GitMirrorOperation.Copy), CancellationToken.None);
        setupResult.Outcome.Should().Be(GitMirrorOutcome.Success);

        var beforeB0Sha = GitTestRepo.ForEachRef(destinationBare, "refs/heads/b0")["refs/heads/b0"];

        _forcePushUnrelatedCommit(sourceBare, "b0", "rewritten-b0");

        var (engine, _, _) = _buildGuardedEngine();
        var result = await engine.ExecuteAsync(
            _request(sourceBare, destinationBare, GitMirrorOperation.Copy), CancellationToken.None);

        result.Outcome.Should().Be(GitMirrorOutcome.DoneWithErrors);
        result.TrippedGuard.Should().Be(GitMirrorGuard.None); // Copy never evaluates the force-push guard - this is the ordinary push-rejection path
        result.Message.Should().Contain("refs/heads/b0");
        result.RefErrors.Should().Contain(e => e.RefName == "refs/heads/b0");

        GitTestRepo.ForEachRef(destinationBare, "refs/heads/b0")["refs/heads/b0"].Should().Be(beforeB0Sha);
    }

    /**
     * AC7: adopt guard - push to a non-empty unmarked destination fails;
     * AllowAdopt succeeds and writes the marker (before the data push, per
     * the command trace); re-pointing at a different source fails even with
     * AllowAdopt (marker mismatch); the marker survives a subsequent Sync.
     */
    [Fact]
    public async Task AC7_AdoptGuardGatesFirstPushAndRejectsAMismatchedRepoint()
    {
        var sourceBare = _initSourceWithBranches("source", 2); // b0, b1
        var destinationBare = _env.NewPath("dest.git");
        GitTestRepo.InitBare(destinationBare);

        // Non-empty AND unmarked: a branch pushed directly, never through the engine.
        _forcePushUnrelatedCommit(destinationBare, "preexisting", "preexisting content");

        var beforeAdoptSnapshot = GitTestRepo.ForEachRefRaw(destinationBare);

        // 1) push to the non-empty, unmarked destination fails.
        var (failEngine, _, failLogger) = _buildGuardedEngine();
        var failResult = await failEngine.ExecuteAsync(
            _request(sourceBare, destinationBare, GitMirrorOperation.Copy), CancellationToken.None);

        failResult.Outcome.Should().Be(GitMirrorOutcome.Failure);
        failResult.TrippedGuard.Should().Be(GitMirrorGuard.AdoptUnmarked);
        GitTestRepo.ForEachRefRaw(destinationBare).Should().Be(beforeAdoptSnapshot);
        failLogger.Entries.Should()
            .Contain(e => e.EventId == GitGuardEvents.AdoptUnmarked && e.Level == LogLevel.Warning);

        // 2) with AllowAdopt it succeeds, and the marker is written BEFORE the data push.
        var (adoptEngine, adoptTrace, _) = _buildGuardedEngine();
        var adoptResult = await adoptEngine.ExecuteAsync(
            _request(sourceBare, destinationBare, GitMirrorOperation.Copy, allowAdopt: true), CancellationToken.None);
        adoptResult.Outcome.Should().Be(GitMirrorOutcome.Success, adoptResult.Message);

        var expectedMarkerRef = _expectedMarkerRefName(sourceBare);
        GitTestRepo.ForEachRef(destinationBare, "refs/backer/mirror-of/*").Should().ContainKey(expectedMarkerRef);

        var pushRecords = adoptTrace.Records.Where(r => r.Arguments.Count > 0 && r.Arguments[0] == "push").ToArray();
        var markerPushIndex = Array.FindIndex(
            pushRecords, r => r.Arguments.Any(a => a.EndsWith(":" + expectedMarkerRef, StringComparison.Ordinal)));
        var dataPushIndex = Array.FindIndex(
            pushRecords, r => r.Arguments.Contains("refs/heads/*:refs/heads/*"));
        markerPushIndex.Should().BeGreaterThanOrEqualTo(0, "the marker push must have happened");
        dataPushIndex.Should().BeGreaterThanOrEqualTo(0, "the data push must have happened");
        markerPushIndex.Should().BeLessThan(dataPushIndex, "the marker must be written before the first data push");

        // 3) re-pointing this destination at a DIFFERENT source fails, even with AllowAdopt.
        var otherSourceBare = _initSourceWithBranches("other-source", 2);
        var afterAdoptSnapshot = GitTestRepo.ForEachRefRaw(destinationBare);

        var (mismatchEngine, _, mismatchLogger) = _buildGuardedEngine();
        var mismatchResult = await mismatchEngine.ExecuteAsync(
            _request(otherSourceBare, destinationBare, GitMirrorOperation.Copy, allowAdopt: true),
            CancellationToken.None);

        mismatchResult.Outcome.Should().Be(GitMirrorOutcome.Failure);
        mismatchResult.TrippedGuard.Should().Be(GitMirrorGuard.AdoptMismatch);
        mismatchResult.Message.Should().Contain(expectedMarkerRef);
        GitTestRepo.ForEachRefRaw(destinationBare).Should().Be(afterAdoptSnapshot);
        mismatchLogger.Entries.Should()
            .Contain(e => e.EventId == GitGuardEvents.AdoptMismatch && e.Level == LogLevel.Warning);

        // 4) the marker survives a subsequent Sync against the ORIGINAL source.
        var (syncEngine, _, _) = _buildGuardedEngine();
        var syncResult = await syncEngine.ExecuteAsync(
            _request(sourceBare, destinationBare, GitMirrorOperation.Sync), CancellationToken.None);
        syncResult.Outcome.Should().Be(GitMirrorOutcome.Success, syncResult.Message);
        GitTestRepo.ForEachRef(destinationBare, "refs/backer/mirror-of/*").Should().ContainKey(expectedMarkerRef);
    }

    /** Run-time self-mirror guard: source==destination fails, no fetch/push - only the two ls-remote probes. */
    [Fact]
    public async Task RunTimeSelfMirrorGuardFailsWithNoPushAndOnlyTheTwoProbesRecorded()
    {
        var sourceBare = _initSourceWithBranches("source", 2);

        var (engine, trace, logger) = _buildGuardedEngine();
        var request = _request(sourceBare, sourceBare, GitMirrorOperation.Copy);
        var result = await engine.ExecuteAsync(request, CancellationToken.None);

        result.Outcome.Should().Be(GitMirrorOutcome.Failure);
        result.TrippedGuard.Should().Be(GitMirrorGuard.SelfMirror);
        result.TransferPerformed.Should().BeFalse();

        trace.Records.Should().HaveCount(2); // source probe + destination probe - no marker probe, no fetch, no push
        trace.Records.Should().OnlyContain(r => r.Arguments.Count > 0 && r.Arguments[0] == "ls-remote");
        logger.Entries.Should().Contain(e => e.EventId == GitGuardEvents.SelfMirror && e.Level == LogLevel.Warning);
    }

    /** AC9 sanity check: the six guard EventIds are pairwise distinct. */
    [Fact]
    public void GitGuardEvents_AllSixEventIdsAreDistinct()
    {
        var ids = new[]
        {
            GitGuardEvents.ZeroRef.Id, GitGuardEvents.SelfMirror.Id, GitGuardEvents.AdoptUnmarked.Id,
            GitGuardEvents.AdoptMismatch.Id, GitGuardEvents.Shrink.Id, GitGuardEvents.ForcePush.Id
        };

        ids.Should().OnlyHaveUniqueItems();
    }

    private string _expectedMarkerRefName(string sourceRemoteUrl)
    {
        var normalized = GitRemoteUrl.Normalize(sourceRemoteUrl, null);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        return "refs/backer/mirror-of/" + hash;
    }

    private (GitMirrorEngine Engine, RecordingGitCommandTrace Trace, CapturingLogger<GitMirrorEngine> Logger) _buildGuardedEngine()
    {
        var trace = new RecordingGitCommandTrace();
        var logger = new CapturingLogger<GitMirrorEngine>();
        var runner = new GitCliRunner(_env.Options, trace);
        var cacheManager = new GitCacheManager(_env.Options, runner);
        var engine = new GitMirrorEngine(_env.Options, runner, cacheManager, logger);
        return (engine, trace, logger);
    }

    private static GitMirrorRequest _request(
        string sourceRemoteUrl, string destinationRemoteUrl, GitMirrorOperation operation,
        bool allowAdopt = false, bool allowUnsafeRefChange = false) => new()
    {
        Source = new GitMirrorEndpoint { RemoteUrl = sourceRemoteUrl },
        Destination = new GitMirrorEndpoint { RemoteUrl = destinationRemoteUrl },
        Operation = operation,
        UserId = "user1",
        SourceUriSchema = "local",
        AllowAdopt = allowAdopt,
        AllowUnsafeRefChange = allowUnsafeRefChange
    };

    /** A bare repo with <paramref name="branchCount"/> branches (b0..bN-1), each a distinct commit off b0. */
    private string _initSourceWithBranches(string name, int branchCount)
    {
        var bare = _env.NewPath($"{name}.git");
        GitTestRepo.InitBare(bare);

        var work = _env.NewPath($"{name}-work-{Guid.NewGuid():N}");
        GitTestRepo.CloneToWorkdir(bare, work);
        GitTestRepo.CreateOrphanBranch(work, "b0");
        GitTestRepo.Commit(work, "f.txt", "b0", "commit b0");
        for (var i = 1; i < branchCount; i++)
        {
            GitTestRepo.CreateBranch(work, $"b{i}", "b0");
            GitTestRepo.Commit(work, "f.txt", $"b{i}", $"commit b{i}");
        }

        GitTestRepo.Push(work, "refs/heads/*:refs/heads/*");
        return bare;
    }

    /** Force-pushes a brand-new, unrelated single-commit history onto <paramref name="branchName"/> in <paramref name="bareRepoPath"/>, bypassing the engine entirely. */
    private string _forcePushUnrelatedCommit(string bareRepoPath, string branchName, string content)
    {
        var tmpWork = _env.NewPath($"unrelated-{Guid.NewGuid():N}");
        GitTestRepo.Run(_env.RootDir, "init", "--quiet", tmpWork);
        GitTestRepo.Run(tmpWork, "config", "user.name", "Backer Test");
        GitTestRepo.Run(tmpWork, "config", "user.email", "backer-test@example.invalid");
        File.WriteAllText(Path.Combine(tmpWork, "f.txt"), content);
        GitTestRepo.Run(tmpWork, "add", "--", "f.txt");
        GitTestRepo.Run(tmpWork, "commit", "--quiet", "-m", "unrelated history");
        GitTestRepo.Run(tmpWork, "push", "--quiet", "--force", bareRepoPath, $"HEAD:{branchName}");
        return GitTestRepo.Run(tmpWork, "rev-parse", "HEAD").Trim();
    }
}
