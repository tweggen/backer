using System.Net;
using FluentAssertions;
using NSubstitute;

namespace Tools.Tests;

public class AddTokenHandlerTests
{
    private static (HttpClient client, RecordingHandler inner) CreateClient(ITokenProvider tokenProvider)
    {
        var inner = new RecordingHandler();
        var handler = new AddTokenHandler(tokenProvider) { InnerHandler = inner };
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        return (client, inner);
    }

    [Fact]
    public async Task AttachesBearerToken_WhenProviderReturnsAToken()
    {
        var tokenProvider = Substitute.For<ITokenProvider>();
        tokenProvider.GetToken().Returns(Task.FromResult<string?>("token-abc"));

        var (client, inner) = CreateClient(tokenProvider);
        inner.Respond(HttpStatusCode.OK);

        var response = await client.GetAsync("/api/thing");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.SendCount.Should().Be(1);
        inner.Requests[0].AuthorizationScheme.Should().Be("Bearer");
        inner.Requests[0].AuthorizationParameter.Should().Be("token-abc");
    }

    [Fact]
    public async Task AttachesNothing_WhenProviderReturnsNull()
    {
        var tokenProvider = Substitute.For<ITokenProvider>();
        tokenProvider.GetToken().Returns(Task.FromResult<string?>(null));

        var (client, inner) = CreateClient(tokenProvider);
        inner.Respond(HttpStatusCode.OK);

        await client.GetAsync("/api/thing");

        inner.SendCount.Should().Be(1);
        inner.Requests[0].AuthorizationScheme.Should().BeNull();
        inner.Requests[0].AuthorizationParameter.Should().BeNull();
    }

    [Fact]
    public async Task AttachesNothing_WhenProviderReturnsEmptyString()
    {
        var tokenProvider = Substitute.For<ITokenProvider>();
        tokenProvider.GetToken().Returns(Task.FromResult<string?>(string.Empty));

        var (client, inner) = CreateClient(tokenProvider);
        inner.Respond(HttpStatusCode.OK);

        await client.GetAsync("/api/thing");

        inner.SendCount.Should().Be(1);
        inner.Requests[0].AuthorizationScheme.Should().BeNull();
        inner.Requests[0].AuthorizationParameter.Should().BeNull();
    }
}
