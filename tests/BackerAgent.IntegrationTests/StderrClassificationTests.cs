using FluentAssertions;
using TestSupport.Agent;
using Xunit;

namespace BackerAgent.IntegrationTests;

/// <summary>
/// Gate 2 acceptance, deferred here from Gate 1: how the agent reads rclone's
/// stderr.
///
/// <para>This is the trigger for an early re-authentication - three
/// token-related error lines make the polling loop refresh tokens instead of
/// waiting out the five-minute inactivity timeout - and it had no test at all.
/// It could not have one either, because the reading loop is bound to a live
/// child process; the classification is now split into
/// <c>_handleStderrLine</c> so it can be driven directly.</para>
/// </summary>
public class StderrClassificationTests
{
    private static async Task<AgentHostFactory> StartedAgentAsync()
    {
        var factory = await AgentHostFactory.CreateAsync();
        await factory.StartAgentAsync();
        return factory;
    }

    [Fact]
    public async Task AnErrorLineIsCollectedWithoutItsPrefix()
    {
        using var factory = await StartedAgentAsync();
        var service = factory.Service;

        service._handleStderrLine(
            "2026/08/20 12:00:00 ERROR : holiday.jpg: failed to copy: permission denied");

        service._stderrErrors.Should().ContainSingle()
            .Which.Should().Be("holiday.jpg: failed to copy: permission denied");
    }

    [Fact]
    public async Task ALineThatIsNotAnErrorIsIgnoredEntirely()
    {
        using var factory = await StartedAgentAsync();
        var service = factory.Service;

        var tokenErrorsBefore = service._stderrTokenErrorCount;

        service._handleStderrLine("2026/08/20 12:00:00 INFO  : holiday.jpg: Copied (new)");
        service._handleStderrLine("Transferred: 1 / 1, 100%");

        service._stderrErrors.Should().BeEmpty();
        service._stderrTokenErrorCount.Should().Be(tokenErrorsBefore);
    }

    [Theory]
    [InlineData("ERROR : onedrive: couldn't fetch token: invalid_grant")]
    [InlineData("ERROR : onedrive: maybe token expired? - try refreshing")]
    public async Task ATokenErrorIsCounted(string line)
    {
        using var factory = await StartedAgentAsync();
        var service = factory.Service;

        var before = service._stderrTokenErrorCount;

        service._handleStderrLine(line);

        service._stderrTokenErrorCount.Should().Be(before + 1);
    }

    [Fact]
    public async Task AnOrdinaryErrorDoesNotCountAsATokenError()
    {
        using var factory = await StartedAgentAsync();
        var service = factory.Service;

        var before = service._stderrTokenErrorCount;

        service._handleStderrLine("ERROR : holiday.jpg: failed to copy: permission denied");

        service._stderrErrors.Should().ContainSingle();
        service._stderrTokenErrorCount.Should().Be(before);
    }

    [Fact]
    public async Task TheErrorBufferIsCappedSoALongRunCannotGrowWithoutBound()
    {
        using var factory = await StartedAgentAsync();
        var service = factory.Service;

        for (var i = 0; i < 250; i++)
        {
            service._handleStderrLine($"ERROR : file{i}.txt: failed to copy");
        }

        service._stderrErrors.Should().HaveCount(200);
        service._stderrErrors.Should().NotContain("file0.txt: failed to copy");
        service._stderrErrors.Last().Should().Be("file249.txt: failed to copy");
    }
}
