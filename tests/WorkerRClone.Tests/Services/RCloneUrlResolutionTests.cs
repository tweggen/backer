using FluentAssertions;
using WorkerRClone.Services;
using Xunit;

namespace WorkerRClone.Tests.Services;

/// <summary>
/// Gate 2 of <c>docs/plan-e2e-test-harness.md</c>: how the agent decides where
/// rclone's remote control lives.
///
/// <para>Before this work the address was effectively hardcoded. The URL
/// rclone prints on startup was parsed into a local variable and then dropped
/// (<c>RCloneService.cs</c>, <c>_startRCloneProcess</c>), so an rclone bound to
/// any other port was unreachable - a real defect, not only a test
/// inconvenience.</para>
/// </summary>
public class RCloneUrlResolutionTests
{
    [Fact]
    public void TheDefaultIsStillTheWellKnownLocalPort()
    {
        /*
         * Production configs rely on this default. If it ever changes, that
         * should be a deliberate edit to this assertion.
         */
        RCloneService._defaultRCloneUrl.Should().Be("http://localhost:5572");
    }

    [Fact]
    public void WithNothingConfigured_TheDiscoveredAddressWins()
    {
        RCloneService._resolveRCloneUrl(null, RCloneService._defaultRCloneUrl)
            .Should().Be("http://localhost:5572");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankConfiguredAddressIsIgnored(string? configured)
    {
        RCloneService._resolveRCloneUrl(configured, "http://127.0.0.1:9999/")
            .Should().Be("http://127.0.0.1:9999/");
    }

    [Fact]
    public void AConfiguredAddressWinsOverTheDiscoveredOne()
    {
        RCloneService._resolveRCloneUrl("http://127.0.0.1:5555/", "http://127.0.0.1:9999/")
            .Should().Be("http://127.0.0.1:5555/");
    }

    [Theory]
    [InlineData("127.0.0.1:5572", "http://127.0.0.1:5572/")]
    [InlineData("192.168.11.5:15572", "http://192.168.11.5:15572/")]
    public void TheAddressRclonePrintsIsTurnedIntoAnAbsoluteUrl(string hostAndPort, string expected)
    {
        /*
         * The startup banner gives host:port with no scheme. Feeding that
         * straight to HttpClient's BaseAddress throws, which is why the
         * original assignment could not have worked even if it had been kept.
         */
        RCloneService._toRCloneUrl(hostAndPort).Should().Be(expected);
    }

    [Theory]
    [InlineData("not a host")]
    [InlineData("")]
    public void AnUnusableAddressIsRejectedRatherThanGuessedAt(string hostAndPort)
    {
        RCloneService._toRCloneUrl(hostAndPort).Should().BeNull();
    }
}
