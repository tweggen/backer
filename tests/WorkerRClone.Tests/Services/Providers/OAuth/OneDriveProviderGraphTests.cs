using System.Net;
using FluentAssertions;
using Hannibal.Models;
using Microsoft.Extensions.Logging.Abstractions;
using WorkerRClone.Services;
using WorkerRClone.Services.Providers.OAuth;
using WorkerRClone.Tests.TestSupport;
using Xunit;

namespace WorkerRClone.Tests.Services.Providers.OAuth;

/// <summary>
/// Offline tests for <see cref="OneDriveProvider.GetDriveInfoAsync"/>.
/// <para>
/// Every test drives the provider through a <see cref="StubHttpMessageHandler"/>, which
/// has no inner handler and never calls <c>base.SendAsync</c>. There is therefore no code
/// path in this file that can reach the network - "no network access" holds by construction,
/// not by convention.
/// </para>
/// </summary>
public class OneDriveProviderGraphTests
{
    /// <summary>
    /// The one and only place a <see cref="OneDriveProvider"/> is constructed in the tests,
    /// so an upstream constructor change only has to be fixed here.
    /// </summary>
    private static OneDriveProvider CreateProvider(IHttpClientFactory? httpClientFactory)
    {
        return new OneDriveProvider(
            NullLogger<OneDriveProvider>.Instance,
            oauth2ClientFactory: null!,
            serviceScopeFactory: null!,
            httpClientFactory: httpClientFactory);
    }

    private static (OneDriveProvider Provider, StubHttpMessageHandler Handler, StubHttpClientFactory Factory)
        CreateProviderWith(HttpStatusCode statusCode, string? jsonBody, Uri? baseAddress = null)
    {
        var handler = StubHttpMessageHandler.Returning(statusCode, jsonBody);
        var factory = new StubHttpClientFactory(handler, baseAddress ?? OneDriveProvider.GraphBaseAddress);
        return (CreateProvider(factory), handler, factory);
    }

    private const string ValidDriveJson =
        """{"id":"b!abc123DRIVE","driveType":"personal","owner":{"user":{"displayName":"Test User"}}}""";

    [Fact]
    public async Task GetDriveInfoAsync_SendsGetToTheGraphDriveEndpoint()
    {
        var (provider, handler, _) = CreateProviderWith(HttpStatusCode.OK, ValidDriveJson);

        await provider.GetDriveInfoAsync("token-abc", CancellationToken.None);

        handler.Requests.Should().HaveCount(1);
        handler.LastRequest!.Method.Should().Be(HttpMethod.Get);
        handler.LastRequest.RequestUri!.AbsoluteUri
            .Should().Be("https://graph.microsoft.com/v1.0/me/drive");
        handler.LastRequest.RequestUri.AbsolutePath.Should().Be("/v1.0/me/drive");
    }

    [Fact]
    public async Task GetDriveInfoAsync_SendsBearerAuthorizationHeaderWithTheAccessToken()
    {
        var (provider, handler, _) = CreateProviderWith(HttpStatusCode.OK, ValidDriveJson);

        await provider.GetDriveInfoAsync("the-access-token", CancellationToken.None);

        var authorization = handler.LastRequest!.Headers.Authorization;
        authorization.Should().NotBeNull();
        authorization!.Scheme.Should().Be("Bearer");
        authorization.Parameter.Should().Be("the-access-token");
        handler.LastRequest.Headers.Authorization!.ToString().Should().Be("Bearer the-access-token");
    }

    [Fact]
    public async Task GetDriveInfoAsync_UsesTheNamedGraphHttpClient()
    {
        var (provider, _, factory) = CreateProviderWith(HttpStatusCode.OK, ValidDriveJson);

        await provider.GetDriveInfoAsync("token", CancellationToken.None);

        factory.RequestedClientNames.Should().ContainSingle().Which.Should().Be("msgraph");
        OneDriveProvider.GraphHttpClientName.Should().Be("msgraph");
    }

    [Fact]
    public async Task GetDriveInfoAsync_TargetsGraphEvenWhenTheInjectedClientHasNoBaseAddress()
    {
        var (provider, handler, _) = CreateProviderWith(HttpStatusCode.OK, ValidDriveJson, baseAddress: null);

        await provider.GetDriveInfoAsync("token", CancellationToken.None);

        handler.LastRequest!.RequestUri!.AbsoluteUri
            .Should().Be("https://graph.microsoft.com/v1.0/me/drive");
    }

    [Fact]
    public async Task GetDriveInfoAsync_WellFormedResponse_ReturnsIdAndDriveType()
    {
        var (provider, _, _) = CreateProviderWith(HttpStatusCode.OK, ValidDriveJson);

        var (driveId, driveType) = await provider.GetDriveInfoAsync("token", CancellationToken.None);

        driveId.Should().Be("b!abc123DRIVE");
        driveType.Should().Be("personal");
    }

    [Fact]
    public async Task GetDriveInfoAsync_Unauthorized_ThrowsDescriptiveMicrosoftGraphException()
    {
        var (provider, _, _) = CreateProviderWith(
            HttpStatusCode.Unauthorized,
            """{"error":{"code":"InvalidAuthenticationToken","message":"Access token has expired."}}""");

        var act = () => provider.GetDriveInfoAsync("expired-token", CancellationToken.None);

        var exception = (await act.Should().ThrowAsync<MicrosoftGraphException>()).Which;

        exception.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        exception.IsTokenRejected.Should().BeTrue();
        exception.Message.Should().Contain("401");
        exception.Message.Should().Contain("Unauthorized");
        exception.Message.Should().Contain("access token was rejected by Microsoft Graph");
        exception.Message.Should().Contain("re-authenticated");
        // The provider surface the provider's own error text, not a bare "Response status code
        // does not indicate success" from EnsureSuccessStatusCode.
        exception.Message.Should().NotContain("Response status code does not indicate success");
    }

    [Fact]
    public async Task GetDriveInfoAsync_Forbidden_ThrowsDescriptiveMicrosoftGraphException()
    {
        var (provider, _, _) = CreateProviderWith(HttpStatusCode.Forbidden, """{"error":{"code":"accessDenied"}}""");

        var exception = (await FluentActions
                .Awaiting(() => provider.GetDriveInfoAsync("token", CancellationToken.None))
                .Should().ThrowAsync<MicrosoftGraphException>())
            .Which;

        exception.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        exception.IsTokenRejected.Should().BeTrue();
        exception.Message.Should().Contain("403");
        exception.Message.Should().Contain("access token was rejected by Microsoft Graph");
    }

    [Fact]
    public async Task GetDriveInfoAsync_ServerError_ThrowsGraphExceptionNamingTheStatusButNotTokenRejection()
    {
        var (provider, _, _) = CreateProviderWith(HttpStatusCode.ServiceUnavailable, "backend busy");

        var exception = (await FluentActions
                .Awaiting(() => provider.GetDriveInfoAsync("token", CancellationToken.None))
                .Should().ThrowAsync<MicrosoftGraphException>())
            .Which;

        exception.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        exception.IsTokenRejected.Should().BeFalse();
        exception.Message.Should().Contain("503");
        exception.Message.Should().NotContain("access token was rejected");
    }

    [Fact]
    public async Task GetDriveInfoAsync_ErrorMessage_NeverContainsTheAccessToken()
    {
        const string secret = "super-secret-access-token-value";
        var (provider, _, _) = CreateProviderWith(HttpStatusCode.Unauthorized, """{"error":{"code":"x"}}""");

        var exception = (await FluentActions
                .Awaiting(() => provider.GetDriveInfoAsync(secret, CancellationToken.None))
                .Should().ThrowAsync<MicrosoftGraphException>())
            .Which;

        exception.ToString().Should().NotContain(secret);
    }

    [Fact]
    public async Task GetDriveInfoAsync_ResponseWithoutId_ThrowsInvalidOperationException()
    {
        var (provider, _, _) = CreateProviderWith(HttpStatusCode.OK, """{"driveType":"personal"}""");

        var exception = (await FluentActions
                .Awaiting(() => provider.GetDriveInfoAsync("token", CancellationToken.None))
                .Should().ThrowAsync<InvalidOperationException>())
            .Which;

        exception.Should().NotBeOfType<MicrosoftGraphException>();
        exception.Message.Should().Contain("drive ID");
        exception.Message.Should().Contain("'id'");
    }

    [Fact]
    public async Task GetDriveInfoAsync_ResponseWithNullId_ThrowsInvalidOperationException()
    {
        var (provider, _, _) = CreateProviderWith(HttpStatusCode.OK, """{"id":null,"driveType":"personal"}""");

        await FluentActions
            .Awaiting(() => provider.GetDriveInfoAsync("token", CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*'id'*");
    }

    [Fact]
    public async Task GetDriveInfoAsync_ResponseWithoutDriveType_ThrowsInvalidOperationException()
    {
        var (provider, _, _) = CreateProviderWith(HttpStatusCode.OK, """{"id":"b!abc"}""");

        await FluentActions
            .Awaiting(() => provider.GetDriveInfoAsync("token", CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*'driveType'*");
    }

    [Fact]
    public async Task GetDriveInfoAsync_NonJsonBody_ThrowsInvalidOperationException()
    {
        var (provider, _, _) = CreateProviderWith(HttpStatusCode.OK, "<html>not json</html>");

        await FluentActions
            .Awaiting(() => provider.GetDriveInfoAsync("token", CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*non-JSON*");
    }

    [Fact]
    public async Task BuildRCloneParametersAsync_WithoutAccessToken_MakesNoHttpCallAtAll()
    {
        var (provider, handler, _) = CreateProviderWith(HttpStatusCode.OK, ValidDriveJson);
        var state = new StorageState { Storage = new Storage { UriSchema = "onedrive", AccessToken = "" } };

        var parameters = await provider.BuildRCloneParametersAsync(state, CancellationToken.None);

        parameters.Should().BeEmpty();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task BuildRCloneParametersAsync_WithAccessToken_UsesTheDriveInfoFromGraph()
    {
        var (provider, handler, _) = CreateProviderWith(HttpStatusCode.OK, ValidDriveJson);
        var state = new StorageState
        {
            Storage = new Storage
            {
                UriSchema = "onedrive",
                ClientId = "client-id",
                ClientSecret = "client-secret",
                AccessToken = "access-token",
                RefreshToken = "refresh-token",
                ExpiresAt = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            }
        };

        var parameters = await provider.BuildRCloneParametersAsync(state, CancellationToken.None);

        parameters["type"].Should().Be("onedrive");
        parameters["drive_id"].Should().Be("b!abc123DRIVE");
        parameters["drive_type"].Should().Be("personal");
        handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public void Provider_IsStillConstructible_WithoutAnHttpClientFactory()
    {
        // The IHttpClientFactory parameter is optional so the type stays usable in
        // contexts that do not register one.
        var act = () => CreateProvider(httpClientFactory: null);

        act.Should().NotThrow();
    }
}
