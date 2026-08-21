using System.Diagnostics;
using BackerAgent.IntegrationTests.TestSupport;
using FluentAssertions;
using Hannibal.Client;
using Hannibal.Models;
using NSubstitute;
using TestSupport.Agent;
using Xunit;

namespace BackerAgent.IntegrationTests;

/// <summary>
/// Gate D AC14 (docs/plan-git-repo-storage.md): <c>GitWorkerService.StopAsync</c>
/// reports every in-flight git job back to Hannibal as <c>DoneFailure</c> and
/// kills its child git process - shutdown parity with
/// <c>RCloneService.cs:2043-2102</c>. Same "held stub" trick as
/// <see cref="GitJobAbortTests"/>: the job's git child process is guaranteed
/// still in flight (blocked on its first real call, <c>ls-remote</c>) at the
/// moment the host is stopped.
/// </summary>
public class GitJobShutdownTests
{
    [Fact]
    public async Task StopAsync_InFlightGitJob_ReportsDoneFailure_AndKillsChildProcess()
    {
        var repoDir = Directory.CreateTempSubdirectory("backer-git-shutdown-").FullName;
        var sourceBare = Path.Combine(repoDir, "source.git");
        GitFixtureRepo.InitBare(sourceBare);
        var work = Path.Combine(repoDir, "work");
        GitFixtureRepo.CloneToWorkdir(sourceBare, work);
        GitFixtureRepo.CreateOrphanBranch(work, "main");
        GitFixtureRepo.Commit(work, "a.txt", "hello", "init");
        GitFixtureRepo.Push(work, "refs/heads/*:refs/heads/*");

        var destinationBare = Path.Combine(repoDir, "dest.git");
        GitFixtureRepo.InitBare(destinationBare);

        var stubPath = _writeHoldingGitStub(repoDir);
        var job = GitJobFixture.Build(303, sourceBare, destinationBare, Rule.RuleOperation.Copy);

        var factory = await AgentHostFactory.CreateAsync(new AgentHostOptions
        {
            ConfigureGitWorkerOptions = options => options.GitPath = stubPath
        });

        factory.Hannibal
            .AcquireNextJobAsync(Arg.Is<AcquireParams>(p => p.Capabilities == "git"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Job?>(job), Task.FromResult<Job?>(null));
        factory.Hannibal
            .AcquireNextJobAsync(Arg.Is<AcquireParams>(p => p.Capabilities != "git"), Arg.Any<CancellationToken>())
            .Returns((Job?)null);

        await factory.StartAgentAsync();

        var pingCountBefore = Process.GetProcessesByName("PING").Length;
        await _waitUntilAsync(
            () => Process.GetProcessesByName("PING").Length > pingCountBefore,
            "the stub git process backing job 303 never started");

        // WebApplicationFactory.Dispose() stops the underlying IHost, which
        // blocks on every hosted service's StopAsync (including
        // GitWorkerService's) before returning - so by the time this call
        // returns, shutdown reporting has already happened.
        factory.Dispose();

        factory.Hannibal.ReceivedCalls().Should().Contain(c =>
            c.GetMethodInfo().Name == nameof(IHannibalServiceClient.ReportJobAsync)
            && ((JobStatus)c.GetArguments()[0]!).JobId == 303
            && ((JobStatus)c.GetArguments()[0]!).State == Job.JobState.DoneFailure,
            "StopAsync must report an in-flight git job as DoneFailure, matching RCloneService.StopAsync's behaviour");

        // Kill() is asynchronous even though StopAsync itself already
        // returned - a short bounded poll, not a sleep, covers the gap.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && Process.GetProcessesByName("PING").Length > pingCountBefore)
        {
            await Task.Delay(100);
        }

        Process.GetProcessesByName("PING").Length.Should().Be(pingCountBefore,
            "no orphan git child process may remain after shutdown");
    }

    private static string _writeHoldingGitStub(string dir)
    {
        var path = Path.Combine(dir, "git-stub-shutdown.cmd");
        File.WriteAllText(
            path,
            "@echo off\r\n"
            + "if \"%~1\"==\"--version\" (\r\n"
            + "    echo git version 2.49.0\r\n"
            + "    exit /b 0\r\n"
            + ")\r\n"
            + "ping -n 1200 127.0.0.1 >nul\r\n");
        return path;
    }

    /// <summary>A ceiling on a condition, not a sleep: returns as soon as it holds.</summary>
    private static async Task _waitUntilAsync(Func<bool> condition, string because, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException(because);
    }
}
