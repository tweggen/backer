using FluentAssertions;
using WorkerGit.Services;
using WorkerGit.Tests.TestSupport;
using Xunit;

namespace WorkerGit.Tests;

/**
 * Gate D AC5 (corruption recovery / disk-full) and AC6 (per-cache locking).
 */
public sealed class GitCacheManagerTests : IDisposable
{
    private readonly GitTestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    private static readonly IReadOnlyList<string> _fetchRefspecs =
        GitMirrorEngine.MirroredNamespaces.Select(ns => $"+{ns}:{ns}").ToArray();

    /** AC5 (recovery branch): a destroyed pack is detected, the cache is deleted and re-cloned exactly once, and the fetch then succeeds. */
    [Fact]
    public async Task AC5_CorruptedCacheIsDeletedAndReclonedOnceThenSucceeds()
    {
        var (sourceBare, work) = _seedTinySource();
        var runner = new GitCliRunner(_env.Options);
        var cacheManager = new GitCacheManager(_env.Options, runner);

        var first = await cacheManager.FetchAsync(
            "user1", "local", sourceBare, sourceBare, null, _fetchRefspecs, null, CancellationToken.None);
        first.Success.Should().BeTrue();
        first.RecoveredFromCorruption.Should().BeFalse();

        // gc --auto alone would not pack this handful of loose objects, so
        // force a single pack file into existence before destroying it.
        GitTestRepo.Run(first.CachePath, "repack", "-a", "-d", "-q");
        GitTestRepo.CorruptOnePackFile(first.CachePath);

        // Something new to actually transfer - fetching "nothing changed"
        // never touches the existing pack at all and would prove nothing.
        GitTestRepo.Commit(work, "a.txt", "second revision", "second");
        GitTestRepo.Push(work, "refs/heads/*:refs/heads/*");

        var second = await cacheManager.FetchAsync(
            "user1", "local", sourceBare, sourceBare, null, _fetchRefspecs, null, CancellationToken.None);

        second.Success.Should().BeTrue();
        second.RecoveredFromCorruption.Should().BeTrue();
        second.Message.Should().ContainEquivalentOf("re-cloned");
    }

    /** AC5 (disk-full branch): below MinFreeDiskBytes, a corrupt-looking cache is reported disk-full and left untouched. */
    [Fact]
    public async Task AC5_CorruptedCacheBelowFreeDiskFloorReportsDiskFullAndDoesNotDelete()
    {
        var (sourceBare, work) = _seedTinySource();
        var runner = new GitCliRunner(_env.Options);
        var lowSpaceProbe = new _FixedFreeDiskSpaceProbe(0);
        var cacheManager = new GitCacheManager(_env.Options, runner, lowSpaceProbe);

        var first = await cacheManager.FetchAsync(
            "user1", "local", sourceBare, sourceBare, null, _fetchRefspecs, null, CancellationToken.None);
        first.Success.Should().BeTrue();

        GitTestRepo.Run(first.CachePath, "repack", "-a", "-d", "-q");
        GitTestRepo.CorruptOnePackFile(first.CachePath);
        var corruptedPackFile = Directory.EnumerateFiles(
            Path.Combine(first.CachePath, "objects", "pack"), "*.pack").Single();
        var corruptedContent = File.ReadAllBytes(corruptedPackFile);

        GitTestRepo.Commit(work, "a.txt", "second revision", "second");
        GitTestRepo.Push(work, "refs/heads/*:refs/heads/*");

        var second = await cacheManager.FetchAsync(
            "user1", "local", sourceBare, sourceBare, null, _fetchRefspecs, null, CancellationToken.None);

        second.Success.Should().BeFalse();
        second.DiskFull.Should().BeTrue();
        second.RecoveredFromCorruption.Should().BeFalse();

        Directory.Exists(first.CachePath).Should().BeTrue();
        File.Exists(corruptedPackFile).Should().BeTrue();
        File.ReadAllBytes(corruptedPackFile).Should().Equal(corruptedContent);
    }

    /** AC6: two jobs sharing one source cache run concurrently to two different destinations without error, serialised by the per-cache lock. */
    [Fact]
    public async Task AC6_ConcurrentJobsSharingOneSourceCacheAreSerialisedByTheLock()
    {
        var (sourceBare, _) = _seedTinySource();
        var destinationA = _env.NewPath("dest-a.git");
        var destinationB = _env.NewPath("dest-b.git");
        GitTestRepo.InitBare(destinationA);
        GitTestRepo.InitBare(destinationB);

        var lockTrace = new RecordingGitCacheLockTrace();
        var runner = new GitCliRunner(_env.Options);
        var cacheManager = new GitCacheManager(_env.Options, runner, lockTrace: lockTrace);
        var engineA = new GitMirrorEngine(_env.Options, runner, cacheManager);
        var engineB = new GitMirrorEngine(_env.Options, runner, cacheManager);

        var requestA = new GitMirrorRequest
        {
            Source = new GitMirrorEndpoint { RemoteUrl = sourceBare },
            Destination = new GitMirrorEndpoint { RemoteUrl = destinationA },
            Operation = GitMirrorOperation.Copy,
            UserId = "user1",
            SourceUriSchema = "local"
        };
        var requestB = new GitMirrorRequest
        {
            Source = new GitMirrorEndpoint { RemoteUrl = sourceBare },
            Destination = new GitMirrorEndpoint { RemoteUrl = destinationB },
            Operation = GitMirrorOperation.Copy,
            UserId = "user1",
            SourceUriSchema = "local"
        };

        var taskA = engineA.ExecuteAsync(requestA, CancellationToken.None);
        var taskB = engineB.ExecuteAsync(requestB, CancellationToken.None);
        var results = await Task.WhenAll(taskA, taskB);

        results.Should().OnlyContain(r => r.Outcome == GitMirrorOutcome.Success);

        var events = lockTrace.Events.OrderBy(e => e.AcquiredAt).ToArray();
        events.Should().HaveCountGreaterThanOrEqualTo(2);
        for (var i = 1; i < events.Length; i++)
        {
            events[i].AcquiredAt.Should().BeOnOrAfter(events[i - 1].ReleasedAt);
        }
    }

    private (string SourceBare, string WorkDir) _seedTinySource()
    {
        var sourceBare = _env.NewPath($"source-{Guid.NewGuid():N}.git");
        GitTestRepo.InitBare(sourceBare);
        var work = _env.NewPath($"source-work-{Guid.NewGuid():N}");
        GitTestRepo.CloneToWorkdir(sourceBare, work);
        GitTestRepo.CreateOrphanBranch(work, "main");
        GitTestRepo.Commit(work, "a.txt", "hello", "init");
        GitTestRepo.Push(work, "refs/heads/*:refs/heads/*");
        return (sourceBare, work);
    }

    private sealed class _FixedFreeDiskSpaceProbe : IFreeDiskSpaceProbe
    {
        private readonly long _freeBytes;

        public _FixedFreeDiskSpaceProbe(long freeBytes) => _freeBytes = freeBytes;

        public long GetFreeBytes(string path) => _freeBytes;
    }
}
