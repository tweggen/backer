using FluentAssertions;
using Hannibal.Client;
using Hannibal.Models;
using NSubstitute;
using TestSupport.Agent;
using WorkerGit.Services;
using WorkerRClone.Models;
using Xunit;

namespace BackerAgent.IntegrationTests;

/// <summary>
/// Gate D AC10: the startup <c>git --version</c> probe gates the git
/// capability, and doing so must not affect the rest of the hosted agent -
/// this is the one AC10 assertion ("the agent still reaches Running") that
/// genuinely needs the full hosted <c>BackerAgent</c>
/// (<see cref="AgentHostFactory"/>), unlike GitWorkerService's other
/// mechanisms, which are covered directly in WorkerGit.Tests.
/// </summary>
public class GitCapabilityProbeTests
{
    [Fact]
    public async Task NonexistentGitBinary_AgentStillReachesRunning_AndNeverAcquiresAGitJob()
    {
        using var factory = await AgentHostFactory.CreateAsync(new AgentHostOptions
        {
            ConfigureGitWorkerOptions = options => options.GitPath = "no-such-git-binary-anywhere.exe"
        });

        await factory.StartAgentAsync();

        // rclone unaffected: the agent still reaches Running.
        factory.CurrentState.Should().Be(RCloneServiceState.ServiceState.Running);

        await WaitUntilAsync(
            () => factory.GitService._probeResult != GitVersionProbeResult.NotYetProbed,
            "GitWorkerService's startup probe never completed");

        factory.GitService._probeResult.IsAvailable.Should().BeFalse();

        /*
         * ExecuteAsync returns immediately after a failed probe, before ever
         * subscribing to the hub or calling acquire - so once the probe
         * result above is observed there is no further code path left that
         * could still call it. No settle delay needed.
         */
        _gitAcquireCalls(factory).Should().Be(0,
            "an agent whose git probe failed must never acquire a git job");
    }

    [Fact]
    public async Task RealGitBinary_AtLeastOneGitCapabilityAcquireCallArrives()
    {
        using var factory = await AgentHostFactory.CreateAsync();

        await factory.StartAgentAsync();

        await WaitUntilAsync(
            () => _gitAcquireCalls(factory) >= 1,
            "no git-capability acquisition call ever arrived, although real git is on PATH");

        factory.GitService._probeResult.IsAvailable.Should().BeTrue();

        // The rclone side is unaffected by the git engine existing alongside it.
        factory.CurrentState.Should().Be(RCloneServiceState.ServiceState.Running);
    }

    /// <summary>
    /// Only calls carrying the "git" capability - the rclone service's own
    /// acquire calls (Capabilities="rclone") exist in both tests above and
    /// must not be confused with GitWorkerService's.
    /// </summary>
    private static int _gitAcquireCalls(AgentHostFactory factory) =>
        factory.Hannibal.ReceivedCalls().Count(call =>
            call.GetMethodInfo().Name == nameof(IHannibalServiceClient.AcquireNextJobAsync)
            && call.GetArguments()[0] is AcquireParams { Capabilities: "git" });

    /// <summary>A ceiling on a condition, not a sleep: returns as soon as it holds.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string because, TimeSpan? timeout = null)
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
