using System.Diagnostics;
using FluentAssertions;
using Hannibal.Client;
using NSubstitute;
using WorkerRClone.Models;
using TestSupport.Agent;
using Xunit;

namespace BackerAgent.IntegrationTests;

/// <summary>
/// Gate 2 acceptance: the agent can be hosted in a test, reaches its running
/// state against a stubbed rclone, and never spawns a process while doing so.
/// </summary>
public class AgentHostTests
{
    [Fact]
    public async Task TheAgentStartsAndReachesRunning_AgainstAStubbedRclone()
    {
        using var factory = await AgentHostFactory.CreateAsync();

        await factory.StartAgentAsync();

        factory.CurrentState.Should().Be(RCloneServiceState.ServiceState.Running);
    }

    [Fact]
    public async Task StartingTheAgent_SpawnsNoRcloneProcess()
    {
        var before = RcloneProcessIds();

        using var factory = await AgentHostFactory.CreateAsync();
        await factory.StartAgentAsync();

        var after = RcloneProcessIds();

        after.Except(before).Should().BeEmpty(
            "the agent must never start an rclone process while SkipProcessStart is set");
    }

    [Fact]
    public async Task TheAgentTalksToTheStub_NotToTheDefaultPort()
    {
        using var factory = await AgentHostFactory.CreateAsync();

        await factory.StartAgentAsync();

        /*
         * _haveRCloneProcess probes with config/listremotes, so its arrival is
         * proof the agent resolved the configured URL rather than
         * http://localhost:5572.
         */
        await factory.Stub.WaitForRequestAsync("/config/listremotes");
    }

    /// <summary>
    /// The agent rewrites <c>backer-rclone.conf</c> on startup and after every
    /// backend login. It must do that inside the test's own directory: an
    /// earlier version of this harness rewrote the real one belonging to
    /// whoever ran the suite.
    /// </summary>
    [Fact]
    public async Task TheAgentWritesItsRcloneConfigIntoTheTestDirectory_NotTheRealOne()
    {
        var realConfig = Path.Combine(
            Tools.EnvironmentDetector.GetConfigDir("Backer"), "backer-rclone.conf");
        var realConfigWrittenBefore =
            File.Exists(realConfig) ? File.GetLastWriteTimeUtc(realConfig) : (DateTime?)null;

        using var factory = await AgentHostFactory.CreateAsync();
        await factory.StartAgentAsync();

        var testConfig = Path.Combine(factory.RCloneConfigDirectory, "backer-rclone.conf");

        File.Exists(testConfig).Should().BeTrue(
            "the agent must write its rclone config into the directory the test gave it");

        if (realConfigWrittenBefore is not null)
        {
            File.GetLastWriteTimeUtc(realConfig).Should().Be(realConfigWrittenBefore.Value,
                "a test must never touch the real rclone configuration");
        }
    }

    /// <summary>
    /// The counterpart that keeps the SkipJobAcquisition test honest: without
    /// the switch, reaching Running is followed by a job fetch.
    /// </summary>
    [Fact]
    public async Task WithoutSkipJobAcquisition_TheAgentAsksForWorkOnReachingRunning()
    {
        using var factory = await AgentHostFactory.CreateAsync();
        await factory.StartAgentAsync();

        await WaitUntilAsync(
            () => AcquireCallCount(factory) >= 1,
            "the agent never called AcquireNextJobAsync after reaching Running");
    }

    [Fact]
    public async Task WithSkipJobAcquisition_TheAgentRunsButNeverAsksForWork()
    {
        using var factory = await AgentHostFactory.CreateAsync(
            options => options.SkipJobAcquisition = true);
        await factory.StartAgentAsync();

        /*
         * The initial fetch fires on entering Running. Awaiting the refusal
         * counter proves that path ran and was declined - a "nothing happened
         * yet" snapshot right after Running would pass vacuously.
         */
        await WaitUntilAsync(
            () => Volatile.Read(ref factory.Service._skippedJobFetchCount) >= 1,
            "the fetch path never fired, so the switch was not exercised");

        AcquireCallCount(factory).Should().Be(0,
            "with SkipJobAcquisition set the agent must leave jobs to the user's other agents");
    }

    [Fact]
    public async Task TheAgentShutsDownCleanly()
    {
        var factory = await AgentHostFactory.CreateAsync();
        await factory.StartAgentAsync();

        var dispose = () => factory.Dispose();

        dispose.Should().NotThrow();
    }

    private static int AcquireCallCount(AgentHostFactory factory)
        => factory.Hannibal.ReceivedCalls().Count(call =>
            call.GetMethodInfo().Name == nameof(IHannibalServiceClient.AcquireNextJobAsync));

    /// <summary>
    /// A ceiling on a condition, not a sleep: returns as soon as it holds.
    /// </summary>
    private static async Task WaitUntilAsync(
        Func<bool> condition, string because, TimeSpan? timeout = null)
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

    private static HashSet<int> RcloneProcessIds()
    {
        try
        {
            return Process.GetProcessesByName("rclone").Select(p => p.Id).ToHashSet();
        }
        catch (PlatformNotSupportedException)
        {
            return new HashSet<int>();
        }
    }
}
