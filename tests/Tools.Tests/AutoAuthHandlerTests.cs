using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using NSubstitute;

namespace Tools.Tests;

public class AutoAuthHandlerTests
{
    private static readonly IServiceProvider Services = Substitute.For<IServiceProvider>();

    private static (HttpClient client, RecordingHandler inner) CreateClient(
        IStaticTokenProvider tokenProvider,
        Func<IServiceProvider, CancellationToken, Task<string>> obtainToken)
    {
        var inner = new RecordingHandler();
        var handler = new AutoAuthHandler(Services, tokenProvider, obtainToken) { InnerHandler = inner };
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        return (client, inner);
    }

    [Fact]
    public async Task SuccessfulResponse_PassesThrough_WithoutRefreshing()
    {
        var tokenProvider = new FakeStaticTokenProvider("initial-token");
        var refreshCount = 0;

        var (client, inner) = CreateClient(tokenProvider, (_, _) =>
        {
            refreshCount++;
            return Task.FromResult("should-not-be-used");
        });
        inner.Respond(HttpStatusCode.OK, "ok");

        var response = await client.GetAsync("/api/thing");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        refreshCount.Should().Be(0);
        inner.SendCount.Should().Be(1);
        inner.Requests[0].AuthorizationParameter.Should().Be("initial-token");
        tokenProvider.SetTokenCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Unauthorized_RefreshesToken_AndRetriesExactlyOnce_WithTheNewToken()
    {
        var tokenProvider = new FakeStaticTokenProvider("stale-token");
        var refreshCount = 0;

        var (client, inner) = CreateClient(tokenProvider, (_, _) =>
        {
            refreshCount++;
            return Task.FromResult("fresh-token");
        });
        inner.Respond(HttpStatusCode.Unauthorized)
             .Respond(HttpStatusCode.OK, "ok");

        var response = await client.GetAsync("/api/thing");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        refreshCount.Should().Be(1);
        inner.SendCount.Should().Be(2);
        inner.Requests[0].AuthorizationParameter.Should().Be("stale-token");
        inner.Requests[1].AuthorizationParameter.Should().Be("fresh-token");
        inner.Requests[1].RequestUri.Should().Be(inner.Requests[0].RequestUri);
        tokenProvider.SetTokenCalls.Should().ContainSingle().Which.Should().Be("fresh-token");
    }

    [Fact]
    public async Task Unauthorized_WithEmptyRefreshResult_Returns401_WithoutRetrying()
    {
        var tokenProvider = new FakeStaticTokenProvider("stale-token");
        var refreshCount = 0;

        var (client, inner) = CreateClient(tokenProvider, (_, _) =>
        {
            refreshCount++;
            return Task.FromResult(string.Empty);
        });
        inner.Respond(HttpStatusCode.Unauthorized);

        var response = await client.GetAsync("/api/thing");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.RequestMessage.Should().NotBeNull();
        refreshCount.Should().Be(1);
        inner.SendCount.Should().Be(1);
        tokenProvider.SetTokenCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Unauthorized_RetryOnPostWithBody_SendsTheSameBodyTwice()
    {
        var tokenProvider = new FakeStaticTokenProvider("stale-token");

        var (client, inner) = CreateClient(tokenProvider, (_, _) => Task.FromResult("fresh-token"));
        inner.Respond(HttpStatusCode.Unauthorized)
             .Respond(HttpStatusCode.OK, "ok");

        const string payload = """{"name":"backup-rule","enabled":true}""";
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/rules")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.SendCount.Should().Be(2);
        inner.Requests[0].BodyAsString.Should().Be(payload);
        inner.Requests[1].BodyAsString.Should().Be(payload);
        inner.Requests[1].Method.Should().Be(HttpMethod.Post);
        inner.Requests[1].ContentType.Should().Be("application/json; charset=utf-8");
        inner.Requests[1].AuthorizationParameter.Should().Be("fresh-token");
    }

    /// <summary>
    /// Regression test for the rebuffering bug. A rewindable content
    /// (<see cref="ByteArrayContent"/>/<see cref="StringContent"/>) happens to
    /// survive being handed to a second send, so the bug only shows with a
    /// genuinely single-read content — which is what any streamed upload is.
    /// </summary>
    [Fact]
    public async Task Unauthorized_RetryOnPostWithNonRewindableBody_SendsTheSameBodyTwice()
    {
        var tokenProvider = new FakeStaticTokenProvider("stale-token");

        var (client, inner) = CreateClient(tokenProvider, (_, _) => Task.FromResult("fresh-token"));
        inner.Respond(HttpStatusCode.Unauthorized)
             .Respond(HttpStatusCode.OK, "ok");

        const string payload = """{"name":"backup-rule","enabled":true}""";
        var content = new StreamContent(new ForwardOnlyStream(Encoding.UTF8.GetBytes(payload)));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/rules") { Content = content };

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.SendCount.Should().Be(2);
        inner.Requests[0].BodyAsString.Should().Be(payload);
        inner.Requests[1].BodyAsString.Should().Be(payload);
        inner.Requests[1].ContentType.Should().Be("application/json; charset=utf-8");
        inner.Requests[1].AuthorizationParameter.Should().Be("fresh-token");
    }

    [Fact]
    public async Task SecondUnauthorized_OnTheRetry_IsReturnedAsIs_WithoutAThirdSend()
    {
        var tokenProvider = new FakeStaticTokenProvider("stale-token");
        var refreshCount = 0;

        var (client, inner) = CreateClient(tokenProvider, (_, _) =>
        {
            refreshCount++;
            return Task.FromResult($"fresh-token-{refreshCount}");
        });
        inner.Respond(HttpStatusCode.Unauthorized)
             .Respond(HttpStatusCode.Unauthorized);

        var response = await client.GetAsync("/api/thing");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        inner.SendCount.Should().Be(2);
        refreshCount.Should().Be(1);
        inner.Requests[1].AuthorizationParameter.Should().Be("fresh-token-1");
    }
}
