using System.Diagnostics;
using BackerAgent.IntegrationTests.TestSupport;
using FluentAssertions;
using Hannibal.Client;
using Hannibal.Models;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using NSubstitute;
using TestSupport.Agent;
using Xunit;

namespace BackerAgent.IntegrationTests;

/// <summary>
/// Gate D AC13 (docs/plan-git-repo-storage.md): aborting a git job kills the
/// in-flight child git process, reports <c>Cancelled</c> back to Hannibal,
/// and leaves the destination bare repository ref-identical to its pre-run
/// state - and the abort routing (<c>BackerAgent.Hubs.BackerControlHub.AbortJob</c>,
/// <c>Program.cs</c>'s <c>/jobs/{jobId}/abort</c>) tries
/// <c>GitWorkerService.TryAbortJobAsync</c> first and only falls back to
/// <c>RCloneService.AbortJobAsync</c> when the git service does not own the
/// job.
///
/// <para>The job's git child process is a stub (same "print a version,
/// otherwise hold" technique as <c>WorkerGit.Tests/GitWatchdogTests</c>'s
/// stalling stub) so the job is guaranteed still in flight - held on its
/// first real call, <c>ls-remote</c> against the source - at the moment the
/// test aborts it.</para>
/// </summary>
public class GitJobAbortTests
{
    [Fact]
    public async Task TryAbortJobAsync_InFlightJob_CancelsReportsAndLeavesDestinationUntouched()
    {
        var repoDir = Directory.CreateTempSubdirectory("backer-git-abort-").FullName;
        var (sourceBare, destinationBare) = _seedRepos(repoDir);
        var refsBefore = GitFixtureRepo.ForEachRef(destinationBare);

        var stubPath = _writeHoldingGitStub(repoDir, "abort");
        var job = GitJobFixture.Build(101, sourceBare, destinationBare, Rule.RuleOperation.Copy);

        using var factory = await AgentHostFactory.CreateAsync(new AgentHostOptions
        {
            ConfigureGitWorkerOptions = options => options.GitPath = stubPath
        });
        _configureAcquire(factory, job);

        await factory.StartAgentAsync();

        var pingCountBefore = Process.GetProcessesByName("PING").Length;
        await _waitUntilAsync(
            () => Process.GetProcessesByName("PING").Length > pingCountBefore,
            "the stub git process backing job 101 never started");

        var aborted = await factory.GitService.TryAbortJobAsync(101);
        aborted.Should().BeTrue();

        await _waitUntilAsync(
            () => _reportedStates(factory, 101).Contains(Job.JobState.Cancelled),
            "no Cancelled report for job 101 reached the substitute Hannibal client");

        await _waitUntilPingCountAsync(pingCountBefore,
            "the stub git process is still running after TryAbortJobAsync returned true");

        var refsAfter = GitFixtureRepo.ForEachRef(destinationBare);
        refsAfter.Should().BeEquivalentTo(refsBefore,
            "an aborted job must never have touched the destination");
    }

    [Fact]
    public async Task TryAbortJobAsync_UnknownJobId_ReturnsFalse_AndReportsNothing()
    {
        using var factory = await AgentHostFactory.CreateAsync();
        await factory.StartAgentAsync();

        var result = await factory.GitService.TryAbortJobAsync(9_999_999);

        result.Should().BeFalse();
        factory.Hannibal.ReceivedCalls().Should().NotContain(c =>
            c.GetMethodInfo().Name == nameof(IHannibalServiceClient.ReportJobAsync)
            && ((JobStatus)c.GetArguments()[0]!).JobId == 9_999_999);
    }

    /// <summary>
    /// Extends the mechanism-level proof above with the actual routing path:
    /// <c>BackerControlHub.AbortJob</c>, invoked over a real SignalR
    /// connection against the hosted agent's own hub (same technique
    /// <c>FullLoopHarness</c> uses for the Hannibal hub - a genuine
    /// <see cref="HubConnection"/> over <c>TestServer</c>'s handler, long
    /// polling), reaches <see cref="GitWorkerService"/> first and aborts the
    /// git-owned job without ever touching rclone.
    /// </summary>
    [Fact]
    public async Task BackerControlHub_AbortJob_ForAGitOwnedJob_RoutesToGitWorkerService()
    {
        var repoDir = Directory.CreateTempSubdirectory("backer-git-abort-hub-").FullName;
        var (sourceBare, destinationBare) = _seedRepos(repoDir);
        var refsBefore = GitFixtureRepo.ForEachRef(destinationBare);

        var stubPath = _writeHoldingGitStub(repoDir, "hub-abort");
        var job = GitJobFixture.Build(202, sourceBare, destinationBare, Rule.RuleOperation.Copy);

        using var factory = await AgentHostFactory.CreateAsync(new AgentHostOptions
        {
            ConfigureGitWorkerOptions = options => options.GitPath = stubPath
        });
        _configureAcquire(factory, job);

        await factory.StartAgentAsync();

        var pingCountBefore = Process.GetProcessesByName("PING").Length;
        await _waitUntilAsync(
            () => Process.GetProcessesByName("PING").Length > pingCountBefore,
            "the stub git process backing job 202 never started");

        await using var hubConnection = new HubConnectionBuilder()
            .WithUrl("https://localhost/backercontrolhub", options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        await hubConnection.StartAsync();
        try
        {
            await hubConnection.InvokeAsync("AbortJob", 202);
        }
        finally
        {
            await hubConnection.StopAsync();
        }

        await _waitUntilAsync(
            () => _reportedStates(factory, 202).Contains(Job.JobState.Cancelled),
            "no Cancelled report for job 202 reached the substitute Hannibal client after a hub AbortJob call");

        await _waitUntilPingCountAsync(pingCountBefore,
            "the stub git process is still running after the hub routed AbortJob");

        var refsAfter = GitFixtureRepo.ForEachRef(destinationBare);
        refsAfter.Should().BeEquivalentTo(refsBefore);
    }

    private static (string SourceBare, string DestinationBare) _seedRepos(string repoDir)
    {
        var sourceBare = Path.Combine(repoDir, "source.git");
        GitFixtureRepo.InitBare(sourceBare);
        var work = Path.Combine(repoDir, "work");
        GitFixtureRepo.CloneToWorkdir(sourceBare, work);
        GitFixtureRepo.CreateOrphanBranch(work, "main");
        GitFixtureRepo.Commit(work, "a.txt", "hello", "init");
        GitFixtureRepo.Push(work, "refs/heads/*:refs/heads/*");

        var destinationBare = Path.Combine(repoDir, "dest.git");
        GitFixtureRepo.InitBare(destinationBare);

        return (sourceBare, destinationBare);
    }

    private static void _configureAcquire(AgentHostFactory factory, Job job)
    {
        factory.Hannibal
            .AcquireNextJobAsync(Arg.Is<AcquireParams>(p => p.Capabilities == "git"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Job?>(job), Task.FromResult<Job?>(null));

        factory.Hannibal
            .AcquireNextJobAsync(Arg.Is<AcquireParams>(p => p.Capabilities != "git"), Arg.Any<CancellationToken>())
            .Returns((Job?)null);
    }

    private static List<Job.JobState> _reportedStates(AgentHostFactory factory, int jobId) =>
        factory.Hannibal.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IHannibalServiceClient.ReportJobAsync))
            .Select(c => (JobStatus)c.GetArguments()[0]!)
            .Where(js => js.JobId == jobId)
            .Select(js => js.State)
            .ToList();

    /// <summary>
    /// A .cmd that answers <c>--version</c> immediately (so the agent's
    /// startup probe passes and it advertises the "git" capability) and
    /// holds on anything else - same technique as
    /// <c>WorkerGit.Tests/GitWatchdogTests</c>'s stalling stub, ping being
    /// the process this project's tests can reliably detect by name on
    /// Windows.
    /// </summary>
    private static string _writeHoldingGitStub(string dir, string tag)
    {
        var path = Path.Combine(dir, $"git-stub-{tag}.cmd");
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

    private static async Task _waitUntilPingCountAsync(int expectedCount, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && Process.GetProcessesByName("PING").Length > expectedCount)
        {
            await Task.Delay(100);
        }

        Process.GetProcessesByName("PING").Length.Should().Be(expectedCount, because);
    }
}
