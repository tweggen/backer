using System.Diagnostics;
using System.Runtime.InteropServices;
using FluentAssertions;
using WorkerRClone.Models;
using Xunit;

namespace BackerAgent.IntegrationTests;

/// <summary>
/// Gate 2 acceptance for the process seam: <c>SkipProcessStart</c> must hold
/// even when the probe fails and the agent would otherwise launch rclone.
///
/// <para>Reaching that code path takes a failing probe: the happy-path tests
/// never get there, because the stub answers and
/// <c>_checkRCloneProcessImpl</c> goes straight to Running. Pointing the agent
/// at a closed port makes the probe fail, which is the only route into
/// <c>_startRCloneProcessImpl</c>.</para>
/// </summary>
public class RCloneProcessStartTests
{
    /// <summary>
    /// Port 1 on loopback: nothing listens, and it is not rclone's default
    /// port, so a real rclone running on this machine can neither be contacted
    /// nor have its configuration rewritten by a test.
    /// </summary>
    private const string ClosedPortUrl = "http://127.0.0.1:1/";

    /// <summary>
    /// A real, always-present executable, so that a broken guard would
    /// genuinely spawn something and leave <c>_processRClone</c> non-null.
    /// Pointing at a non-existent binary would prove nothing: the spawn would
    /// throw and leave the field null exactly as the skip does.
    ///
    /// <para>Both shells exit immediately when handed rclone's arguments, so
    /// a failure here does not leave a process behind.</para>
    /// </summary>
    private static string RealExecutable =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine(Environment.SystemDirectory, "cmd.exe")
            : "/bin/sh";

    [Fact]
    public async Task WithSkipProcessStart_NoProcessIsEverLaunched()
    {
        File.Exists(RealExecutable).Should().BeTrue(
            "the test needs a real executable for the assertion to mean anything");

        var before = RcloneProcessIds();

        using var factory = await AgentHostFactory.CreateAsync(options =>
        {
            options.RCloneUrl = ClosedPortUrl;
            options.RClonePath = RealExecutable;
            options.SkipProcessStart = true;
        });

        _ = factory.Services;

        /*
         * RCloneProcessNotFound -> StartRCloneProcess -> (ten one-second
         * probes) -> RCloneProcessStartFailed -> WaitConfig
         * (RCloneStateMachine.cs:75, :87).
         */
        await factory.WaitForStateAsync(
            RCloneServiceState.ServiceState.WaitConfig, TimeSpan.FromSeconds(60));

        factory.Service._processRClone.Should().BeNull(
            "SkipProcessStart must prevent the spawn, not merely let it fail");
        RcloneProcessIds().Except(before).Should().BeEmpty();
    }

    /// <summary>
    /// The agent must not sit in Running with nothing behind it when rclone is
    /// unreachable; it invalidates the configuration instead.
    /// </summary>
    [Fact]
    public async Task WithNoRcloneReachable_TheAgentDoesNotReachRunning()
    {
        using var factory = await AgentHostFactory.CreateAsync(options =>
        {
            options.RCloneUrl = ClosedPortUrl;
        });

        _ = factory.Services;

        await factory.WaitForStateAsync(
            RCloneServiceState.ServiceState.WaitConfig, TimeSpan.FromSeconds(60));

        factory.CurrentState.Should().NotBe(RCloneServiceState.ServiceState.Running);
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
