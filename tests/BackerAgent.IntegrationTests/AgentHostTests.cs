using System.Diagnostics;
using FluentAssertions;
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

    [Fact]
    public async Task TheAgentShutsDownCleanly()
    {
        var factory = await AgentHostFactory.CreateAsync();
        await factory.StartAgentAsync();

        var dispose = () => factory.Dispose();

        dispose.Should().NotThrow();
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
