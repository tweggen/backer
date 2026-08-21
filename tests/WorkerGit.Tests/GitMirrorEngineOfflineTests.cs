using FluentAssertions;
using WorkerGit.Services;
using WorkerGit.Tests.TestSupport;
using Xunit;

namespace WorkerGit.Tests;

/**
 * Gate D's offline mirror-engine acceptance criteria (plan-git-repo-storage.md
 * Gate D AC1-4, AC11, AC12), against real bare repositories in a temp
 * directory - no network, no account.
 */
public sealed class GitMirrorEngineOfflineTests : IDisposable
{
    private readonly GitTestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    /** AC1: exact refs/SHAs mirrored, and only MIRRORED refspecs were ever passed to git. */
    [Fact]
    public async Task AC1_CopyMirrorsExactlyTheMirroredRefsAndNothingOutsideThem()
    {
        var (sourceBare, _, mainSha) = _seedSource();
        var destinationBare = _env.NewPath("dest.git");
        GitTestRepo.InitBare(destinationBare);

        var trace = new RecordingGitCommandTrace();
        var engine = _buildEngine(trace);

        var result = await engine.ExecuteAsync(_copyRequest(sourceBare, destinationBare), CancellationToken.None);

        result.Outcome.Should().Be(GitMirrorOutcome.Success);

        var sourceRefs = GitTestRepo.ForEachRef(sourceBare);
        var destinationRefs = GitTestRepo.ForEachRef(destinationBare);
        destinationRefs.Should().BeEquivalentTo(sourceRefs);
        destinationRefs.Should().HaveCount(5); // main, feature, v1-lw, v1-ann, refs/notes/commits
        destinationRefs.Should().ContainKey("refs/notes/commits");

        var allArguments = trace.Records.SelectMany(r => r.Arguments).ToArray();
        allArguments.Should().NotContain(a => a.Contains("refs/pull", StringComparison.Ordinal));
        allArguments.Should().NotContain("--mirror");
        allArguments.Should().NotContain(a => a == "refs/*:refs/*" || a == "+refs/*:refs/*");

        var pushRecord = trace.Records.Single(r => r.Arguments.Count > 0 && r.Arguments[0] == "push");
        pushRecord.Arguments.Should().Contain("refs/heads/*:refs/heads/*");
        pushRecord.Arguments.Should().Contain("refs/tags/*:refs/tags/*");
        pushRecord.Arguments.Should().Contain("refs/notes/*:refs/notes/*");
        pushRecord.Arguments.Should().NotContain(a => a.StartsWith('+'));

        mainSha.Should().NotBeNullOrEmpty();
    }

    /** AC2: a clone of the destination restores working content and full history. */
    [Fact]
    public async Task AC2_CloneOfDestinationRestoresContentAndHistory()
    {
        var (sourceBare, sourceWork, _) = _seedSource();
        var destinationBare = _env.NewPath("dest.git");
        GitTestRepo.InitBare(destinationBare);

        var engine = _buildEngine(new RecordingGitCommandTrace());
        var result = await engine.ExecuteAsync(_copyRequest(sourceBare, destinationBare), CancellationToken.None);
        result.Outcome.Should().Be(GitMirrorOutcome.Success);

        var restoredWork = _env.NewPath("restored-work");
        GitTestRepo.CloneToWorkdir(destinationBare, restoredWork);
        GitTestRepo.Checkout(restoredWork, "main");

        GitTestRepo.ReadFileAtHead(restoredWork, "a.txt").Should().Be(GitTestRepo.ReadFileAtHead(sourceWork, "a.txt"));

        // Full-history comparison against the destination *bare* repo, not a
        // further clone of it: a plain `git clone` does not fetch
        // refs/notes/* by default (on either side), so comparing two clones'
        // "log --all" would mismatch by the notes commit regardless of
        // whether the mirror itself is correct. The bare-to-bare comparison
        // is what actually proves history was mirrored faithfully.
        var sourceHashes = GitTestRepo.LogAllHashes(sourceBare).OrderBy(h => h);
        var destinationHashes = GitTestRepo.LogAllHashes(destinationBare).OrderBy(h => h);
        destinationHashes.Should().BeEquivalentTo(sourceHashes);
    }

    /** AC3: the already-in-sync fast path performs exactly two ls-remote calls, no fetch, no push. */
    [Fact]
    public async Task AC3_SecondRunWhenAlreadyInSyncOnlyPerformsTwoLsRemoteCalls()
    {
        var (sourceBare, _, _) = _seedSource();
        var destinationBare = _env.NewPath("dest.git");
        GitTestRepo.InitBare(destinationBare);

        var firstEngine = _buildEngine(new RecordingGitCommandTrace());
        var firstResult = await firstEngine.ExecuteAsync(
            _copyRequest(sourceBare, destinationBare), CancellationToken.None);
        firstResult.Outcome.Should().Be(GitMirrorOutcome.Success);

        var secondTrace = new RecordingGitCommandTrace();
        var secondEngine = _buildEngine(secondTrace);
        var secondResult = await secondEngine.ExecuteAsync(
            _copyRequest(sourceBare, destinationBare), CancellationToken.None);

        secondResult.Outcome.Should().Be(GitMirrorOutcome.Success);
        secondResult.TransferPerformed.Should().BeFalse();
        secondResult.Message.Should().Contain("already in sync");

        secondTrace.Records.Should().HaveCount(2);
        secondTrace.Records.Should().OnlyContain(r => r.Arguments.Count > 0 && r.Arguments[0] == "ls-remote");
    }

    /** AC4: a branch deleted on the destination only is restored by the next run (the fingerprint design would have missed this). */
    [Fact]
    public async Task AC4_BranchDeletedOnDestinationOnlyIsRestoredByNextRun()
    {
        var (sourceBare, _, _) = _seedSource();
        var destinationBare = _env.NewPath("dest.git");
        GitTestRepo.InitBare(destinationBare);

        var firstEngine = _buildEngine(new RecordingGitCommandTrace());
        var firstResult = await firstEngine.ExecuteAsync(
            _copyRequest(sourceBare, destinationBare), CancellationToken.None);
        firstResult.Outcome.Should().Be(GitMirrorOutcome.Success);

        GitTestRepo.DeleteRef(destinationBare, "refs/heads/feature");
        GitTestRepo.ForEachRef(destinationBare).Should().NotContainKey("refs/heads/feature");

        var secondEngine = _buildEngine(new RecordingGitCommandTrace());
        var secondResult = await secondEngine.ExecuteAsync(
            _copyRequest(sourceBare, destinationBare), CancellationToken.None);

        secondResult.Outcome.Should().Be(GitMirrorOutcome.Success);
        var destinationRefs = GitTestRepo.ForEachRef(destinationBare);
        destinationRefs.Should().ContainKey("refs/heads/feature");
        destinationRefs["refs/heads/feature"].Should().Be(GitTestRepo.ForEachRef(sourceBare)["refs/heads/feature"]);
    }

    /** AC11: Nop completes Success without a single git invocation. */
    [Fact]
    public async Task AC11_NopCompletesSuccessWithZeroGitInvocations()
    {
        var trace = new RecordingGitCommandTrace();
        var engine = _buildEngine(trace);

        var request = new GitMirrorRequest
        {
            Source = new GitMirrorEndpoint { RemoteUrl = "https://example.invalid/never-contacted.git" },
            Destination = new GitMirrorEndpoint { RemoteUrl = "https://example.invalid/never-contacted-2.git" },
            Operation = GitMirrorOperation.Nop,
            UserId = "user1",
            SourceUriSchema = "local"
        };

        var result = await engine.ExecuteAsync(request, CancellationToken.None);

        result.Outcome.Should().Be(GitMirrorOutcome.Success);
        result.TransferPerformed.Should().BeFalse();
        trace.Records.Should().BeEmpty();
    }

    /** AC12: filter=lfs in .gitattributes at a branch tip ends DoneWithErrors naming LFS. */
    [Fact]
    public async Task AC12_LfsAttributeAtBranchTipEndsDoneWithErrorsNamingLfs()
    {
        var sourceBare = _env.NewPath("source.git");
        GitTestRepo.InitBare(sourceBare);
        var work = _env.NewPath("source-work");
        GitTestRepo.CloneToWorkdir(sourceBare, work);
        GitTestRepo.CreateOrphanBranch(work, "main");
        GitTestRepo.Commit(work, "a.txt", "hello", "init");
        File.WriteAllText(
            Path.Combine(work, ".gitattributes"), "*.bin filter=lfs diff=lfs merge=lfs -text\n");
        GitTestRepo.Run(work, "add", "--", ".gitattributes");
        GitTestRepo.Run(work, "commit", "--quiet", "-m", "track large files with lfs");
        GitTestRepo.Push(work, "refs/heads/*:refs/heads/*");

        var destinationBare = _env.NewPath("dest.git");
        GitTestRepo.InitBare(destinationBare);

        var engine = _buildEngine(new RecordingGitCommandTrace());
        var result = await engine.ExecuteAsync(_copyRequest(sourceBare, destinationBare), CancellationToken.None);

        result.Outcome.Should().Be(GitMirrorOutcome.DoneWithErrors);
        result.Message.Should().ContainEquivalentOf("LFS");
        result.Message.Should().Contain("refs/heads/main");

        // The mirror itself still completed - LFS is a reported limitation, not a transfer failure.
        GitTestRepo.ForEachRef(destinationBare).Should().ContainKey("refs/heads/main");
    }

    /** Two branches, one lightweight tag, one annotated tag, one note - the AC1 fixture. */
    private (string SourceBare, string SourceWork, string MainSha) _seedSource()
    {
        var sourceBare = _env.NewPath("source.git");
        GitTestRepo.InitBare(sourceBare);

        var work = _env.NewPath("source-work");
        GitTestRepo.CloneToWorkdir(sourceBare, work);
        GitTestRepo.CreateOrphanBranch(work, "main");
        var mainSha = GitTestRepo.Commit(work, "a.txt", "hello from main", "init");

        GitTestRepo.CreateBranch(work, "feature", "main");
        GitTestRepo.Commit(work, "b.txt", "hello from feature", "feature work");

        GitTestRepo.Checkout(work, "main");
        GitTestRepo.Tag(work, "v1-lw");
        GitTestRepo.AnnotatedTag(work, "v1-ann", "release 1");
        GitTestRepo.Note(work, mainSha, "a note on the initial commit");

        GitTestRepo.Push(
            work, "refs/heads/*:refs/heads/*", "refs/tags/*:refs/tags/*", "refs/notes/*:refs/notes/*");

        return (sourceBare, work, mainSha);
    }

    private GitMirrorEngine _buildEngine(RecordingGitCommandTrace trace)
    {
        var runner = new GitCliRunner(_env.Options, trace);
        var cacheManager = new GitCacheManager(_env.Options, runner);
        return new GitMirrorEngine(_env.Options, runner, cacheManager);
    }

    private static GitMirrorRequest _copyRequest(string sourceBare, string destinationBare) => new()
    {
        Source = new GitMirrorEndpoint { RemoteUrl = sourceBare },
        Destination = new GitMirrorEndpoint { RemoteUrl = destinationBare },
        Operation = GitMirrorOperation.Copy,
        UserId = "user1",
        SourceUriSchema = "local"
    };
}
