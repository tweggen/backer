using FluentAssertions;
using Hannibal.Client;
using Hannibal.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Tools;
using WorkerGit.Configuration;
using WorkerGit.Services;
using WorkerGit.Tests.TestSupport;
using Xunit;

namespace WorkerGit.Tests;

/**
 * Gate D slice 2: GitWorkerService's own mechanisms (plan-git-repo-storage.md
 * Gate D ACs 8 (mechanism), 10, 11, 13, 15), tested by constructing the
 * service directly against a substituted <see cref="IHannibalServiceClient"/>
 * and two local bare repositories - no ASP.NET host required, which also
 * means log messages are actually observable (BackerAgent's real host
 * replaces the logger factory wholesale via Serilog, so a hosted test cannot
 * assert on log text at all; see AgentHostFactory.ConfigureWebHost's
 * comment). The AC10 "the whole agent still reaches Running" acceptance
 * criterion, which genuinely needs the hosted agent, lives in
 * BackerAgent.IntegrationTests instead.
 */
public sealed class GitWorkerServiceTests : IDisposable
{
    private readonly GitTestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public async Task Probe_NonexistentGitPath_ReportsUnavailable_AndNeverAcquires()
    {
        var options = new GitWorkerOptions
        {
            GitPath = Path.Combine(_env.RootDir, "no-such-git-binary.exe"),
            CacheRoot = _env.CacheRoot
        };
        var (service, hannibal) = _buildService(options);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await _waitUntilAsync(
                () => service._probeResult != GitVersionProbeResult.NotYetProbed,
                "the startup probe never completed");

            service._probeResult.IsAvailable.Should().BeFalse();
            service._probeResult.Reason.Should().NotBeNullOrWhiteSpace();

            /*
             * ExecuteAsync returns immediately after a failed probe, before
             * it ever subscribes to the hub or calls _tryAcquireJobAsync -
             * so by the time the probe result above is observed, there is no
             * further code path left that could still call acquire. No
             * settle delay needed.
             */
            hannibal.ReceivedCalls().Should().NotContain(
                c => c.GetMethodInfo().Name == nameof(IHannibalServiceClient.AcquireNextJobAsync));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Probe_RealGit_AcquiresWithGitCapability()
    {
        var options = new GitWorkerOptions { GitPath = "git", CacheRoot = _env.CacheRoot };
        var (service, hannibal) = _buildService(options);
        hannibal.AcquireNextJobAsync(Arg.Any<AcquireParams>(), Arg.Any<CancellationToken>()).Returns((Job?)null);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await _waitUntilAsync(
                () => hannibal.ReceivedCalls().Any(c =>
                    c.GetMethodInfo().Name == nameof(IHannibalServiceClient.AcquireNextJobAsync)
                    && ((AcquireParams)c.GetArguments()[0]!).Capabilities == "git"),
                "no git-capability acquisition call ever arrived");

            service._probeResult.IsAvailable.Should().BeTrue();
            service._probeResult.Version.Should().NotBeNull();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecutesAcquiredJob_MirrorsBranch_ReportsDoneSuccess_AndFiresProgress()
    {
        var (sourceBare, destinationBare, sha) = _seedRepos();
        var job = _buildJob(42, sourceBare, destinationBare, Rule.RuleOperation.Copy);

        var engineTrace = new RecordingGitCommandTrace();
        var (service, hannibal) = _buildService(_env.Options, engineTrace);
        hannibal.AcquireNextJobAsync(Arg.Any<AcquireParams>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Job?>(job), Task.FromResult<Job?>(null));

        var progressEvents = new List<(int JobId, string Phase)>();
        service.OnJobProgress = (jobId, phase) =>
        {
            lock (progressEvents) { progressEvents.Add((jobId, phase)); }
        };

        await service.StartAsync(CancellationToken.None);
        try
        {
            await _waitUntilAsync(() => _reportedStates(hannibal, 42).Any(), "job 42 was never reported back");

            _reportedStates(hannibal, 42).Should().Contain(Job.JobState.DoneSuccess);

            var destinationRefs = GitTestRepo.ForEachRef(destinationBare);
            destinationRefs.Should().ContainKey("refs/heads/main");
            destinationRefs["refs/heads/main"].Should().Be(sha);

            engineTrace.Records.Should().Contain(r => r.Arguments.Count > 0 && r.Arguments[0] == "push");

            // AC15's mechanism: at least one non-empty progress line during the fetch/push.
            lock (progressEvents)
            {
                progressEvents.Should().Contain(e => e.JobId == 42 && !string.IsNullOrWhiteSpace(e.Phase));
            }

            /*
             * AC8's mechanism, not the real proof: _runHeartbeatLoopAsync is
             * started unconditionally for every acquired job, before the
             * engine call - a local mirror this small finishes in well under
             * the 25s heartbeat interval, so no Executing report is expected
             * here. The real 150s-hold proof against the actual 120s server
             * timeout is slice 3's job, run against BackerApiFactory.
             */
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SyncOperation_ReportsDoneFailure_NamesGateE_AndNeverInvokesTheEngine()
    {
        var (sourceBare, destinationBare, _) = _seedRepos();
        var job = _buildJob(7, sourceBare, destinationBare, Rule.RuleOperation.Sync);

        var engineTrace = new RecordingGitCommandTrace();
        var capturingLogger = new _CapturingLogger<GitWorkerService>();
        var (service, hannibal) = _buildService(_env.Options, engineTrace, capturingLogger);
        hannibal.AcquireNextJobAsync(Arg.Any<AcquireParams>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Job?>(job), Task.FromResult<Job?>(null));

        await service.StartAsync(CancellationToken.None);
        try
        {
            await _waitUntilAsync(() => _reportedStates(hannibal, 7).Any(), "job 7 was never reported back");

            _reportedStates(hannibal, 7).Should().ContainSingle().Which.Should().Be(Job.JobState.DoneFailure);

            engineTrace.Records.Should().BeEmpty(
                "Sync must never reach the engine before Gate E ships the safety guards (plan §5)");

            lock (capturingLogger.Messages)
            {
                capturingLogger.Messages.Should().Contain(m =>
                    m.Contains("Gate E", StringComparison.Ordinal)
                    && m.Contains("safety guard", StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task TryAbortJobAsync_UnknownJob_ReturnsFalse_AndReportsNothing()
    {
        var options = new GitWorkerOptions { GitPath = "git", CacheRoot = _env.CacheRoot };
        var (service, hannibal) = _buildService(options);
        hannibal.AcquireNextJobAsync(Arg.Any<AcquireParams>(), Arg.Any<CancellationToken>()).Returns((Job?)null);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await _waitUntilAsync(
                () => service._probeResult != GitVersionProbeResult.NotYetProbed, "the startup probe never completed");

            var result = await service.TryAbortJobAsync(999);

            result.Should().BeFalse();
            hannibal.ReceivedCalls().Should().NotContain(
                c => c.GetMethodInfo().Name == nameof(IHannibalServiceClient.ReportJobAsync));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private (string SourceBare, string DestinationBare, string BranchSha) _seedRepos()
    {
        var sourceBare = _env.NewPath("source.git");
        GitTestRepo.InitBare(sourceBare);
        var work = _env.NewPath("source-work");
        GitTestRepo.CloneToWorkdir(sourceBare, work);
        GitTestRepo.CreateOrphanBranch(work, "main");
        var sha = GitTestRepo.Commit(work, "a.txt", "hello", "init");
        GitTestRepo.Push(work, "refs/heads/*:refs/heads/*");

        var destinationBare = _env.NewPath("dest.git");
        GitTestRepo.InitBare(destinationBare);

        return (sourceBare, destinationBare, sha);
    }

    private static Job _buildJob(int id, string sourceBare, string destinationBare, Rule.RuleOperation operation)
    {
        var sourceStorage = new Storage
        {
            Id = 1, UserId = "user1", Technology = "git", UriSchema = "srcgit",
            Host = sourceBare, Username = "", Password = ""
        };
        var destinationStorage = new Storage
        {
            Id = 2, UserId = "user1", Technology = "git", UriSchema = "dstgit",
            Host = destinationBare, Username = "", Password = ""
        };

        var sourceEndpoint = new Endpoint
        {
            Id = 1, Name = "src", UserId = "user1", StorageId = 1, Storage = sourceStorage, Path = ""
        };
        var destinationEndpoint = new Endpoint
        {
            Id = 2, Name = "dst", UserId = "user1", StorageId = 2, Storage = destinationStorage, Path = ""
        };

        return new Job
        {
            Id = id,
            UserId = "user1",
            Tag = "test",
            Operation = operation,
            Owner = "",
            State = Job.JobState.Ready,
            SourceEndpointId = 1,
            SourceEndpoint = sourceEndpoint,
            DestinationEndpointId = 2,
            DestinationEndpoint = destinationEndpoint
        };
    }

    private static List<Job.JobState> _reportedStates(IHannibalServiceClient hannibal, int jobId) =>
        hannibal.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IHannibalServiceClient.ReportJobAsync))
            .Select(c => (JobStatus)c.GetArguments()[0]!)
            .Where(js => js.JobId == jobId)
            .Select(js => js.State)
            .ToList();

    private static (GitWorkerService Service, IHannibalServiceClient Hannibal) _buildService(
        GitWorkerOptions options,
        IGitCommandTrace? engineTrace = null,
        ILogger<GitWorkerService>? logger = null)
    {
        var hannibal = Substitute.For<IHannibalServiceClient>();
        var networkIdentifier = Substitute.For<INetworkIdentifier>();
        networkIdentifier.GetCurrentNetwork().Returns("test-network");

        var services = new ServiceCollection();
        services.AddSingleton(hannibal);
        var provider = services.BuildServiceProvider();

        var connections = new Dictionary<string, HubConnection>
        {
            // Never started, same as TestSupport.Agent.AgentHostFactory's default -
            // GitWorkerService only ever calls .On() on it, never .StartAsync().
            ["hannibal"] = new HubConnectionBuilder().WithUrl("http://127.0.0.1:1/hannibal").Build()
        };

        var probeRunner = new GitCliRunner(options);
        var engineRunner = new GitCliRunner(options, engineTrace);
        var engine = new GitMirrorEngine(options, engineRunner, new GitCacheManager(options, engineRunner));

        var service = new GitWorkerService(
            logger ?? NullLogger<GitWorkerService>.Instance,
            options,
            probeRunner,
            engine,
            connections,
            provider.GetRequiredService<IServiceScopeFactory>(),
            networkIdentifier);

        return (service, hannibal);
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

    private sealed class _CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => _NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
            {
                Messages.Add(formatter(state, exception));
            }
        }

        private sealed class _NullScope : IDisposable
        {
            public static readonly _NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
