using System.Diagnostics;
using FluentAssertions;
using WorkerGit.Services;
using WorkerGit.Tests.TestSupport;
using Xunit;

namespace WorkerGit.Tests;

/** Gate D AC9: the stall watchdog kills a hung git process and reports it clearly. */
public sealed class GitWatchdogTests : IDisposable
{
    private readonly GitTestEnvironment _env =
        new(jobTimeout: TimeSpan.FromMinutes(5), stallTimeout: TimeSpan.FromSeconds(3));

    public void Dispose() => _env.Dispose();

    /** A stub "git" that prints one line then hangs; StallTimeout must kill it and leave no orphan process. */
    [Fact]
    public async Task AC9_StalledGitProcessIsKilledAndReportsFailureWithStallMessage()
    {
        _env.Options.GitPath = _writeStallingGitStub();

        var runner = new GitCliRunner(_env.Options);
        var cacheManager = new GitCacheManager(_env.Options, runner);
        var engine = new GitMirrorEngine(_env.Options, runner, cacheManager);

        var request = new GitMirrorRequest
        {
            Source = new GitMirrorEndpoint { RemoteUrl = "https://example.invalid/source.git" },
            Destination = new GitMirrorEndpoint { RemoteUrl = "https://example.invalid/dest.git" },
            Operation = GitMirrorOperation.Copy,
            UserId = "user1",
            SourceUriSchema = "local"
        };

        var pingCountBefore = Process.GetProcessesByName("PING").Length;

        var result = await engine.ExecuteAsync(request, CancellationToken.None);

        result.Outcome.Should().Be(GitMirrorOutcome.Failure);
        result.Message.Should().ContainEquivalentOf("stall");

        // Bounded condition poll, not a fixed sleep: the kill is asynchronous
        // (Kill() then a background reap), so give the OS a moment to catch up.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && Process.GetProcessesByName("PING").Length > pingCountBefore)
        {
            await Task.Delay(100);
        }

        Process.GetProcessesByName("PING").Length.Should().Be(
            pingCountBefore, "the watchdog must kill the whole process tree, not just the .cmd wrapper");
    }

    private string _writeStallingGitStub()
    {
        var path = Path.Combine(_env.RootDir, "git-stub.cmd");
        File.WriteAllText(
            path,
            "@echo off\r\n"
            + "echo starting\r\n"
            + "ping -n 600 127.0.0.1 >nul\r\n");
        return path;
    }
}
