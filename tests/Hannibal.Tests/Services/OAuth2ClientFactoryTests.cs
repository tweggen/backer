using System.Collections.Specialized;
using System.Net;
using System.Reflection;
using FluentAssertions;
using Hannibal.Configuration;
using Hannibal.Tests.TestSupport;
using NSubstitute;
using OAuth2.Client;
using OAuth2.Infrastructure;
using RestSharp;

namespace Hannibal.Tests.Services;

/// <summary>
/// Gate D: the OAuth2 seam. Every test here is offline - the RestSharp transport
/// is either substituted (no socket) or only used for local URI building.
/// </summary>
public class OAuth2ClientFactoryTests
{
    /// <summary>
    /// The literal body Microsoft returns from the token endpoint once the app
    /// registration's client secret has expired - the July 2026 live failure.
    /// </summary>
    private const string InvalidClientBody =
        """
        {"error":"invalid_client","error_description":"AADSTS7000222: The provided client secret keys for app '11111111-2222-3333-4444-555555555555' are expired. Visit the Azure portal to create new keys for your app: https://aka.ms/NewClientSecret.","error_codes":[7000222],"timestamp":"2026-07-14 08:12:33Z","trace_id":"a0a0a0a0-b1b1-c2c2-d3d3-e4e4e4e4e4e4","correlation_id":"f5f5f5f5-0606-1717-2828-393939393939"}
        """;

    private const string TokenEndpointFragment = "/token";
    private const string UserInfoFragment = "/me";

    private static OAuthOptions _onedriveOptions(string? redirectUri = null) => new()
    {
        RedirectUri = redirectUri,
        Providers = new SortedDictionary<string, OAuthProviderOptions>
        {
            ["onedrive"] = new()
            {
                ClientId = "11111111-2222-3333-4444-555555555555",
                ClientSecret = "a-secret"
            }
        }
    };


    // ---------------------------------------------------------------- happy path

    [Fact]
    public async Task HappyPath_TokenExchange_ExposesAccessRefreshTokenAndExpiry()
    {
        var transport = new StubOAuth2Transport()
            .RespondTo(TokenEndpointFragment, HttpStatusCode.OK,
                """
                {"token_type":"Bearer","scope":"Files.ReadWrite User.Read","expires_in":3600,"access_token":"the-access-token","refresh_token":"the-refresh-token"}
                """)
            .RespondTo(UserInfoFragment, HttpStatusCode.OK,
                """
                {"id":"c0ffee","displayName":"Timo Weggen","givenName":"Timo","surname":"Weggen","mail":"timo@example.com","userPrincipalName":"timo@example.com"}
                """);

        var client = _createOnedriveClient(transport);

        var before = DateTime.Now;
        var userInfo = await client.GetUserInfoAsync(_callbackParameters());

        client.AccessToken.Should().Be("the-access-token");
        client.RefreshToken.Should().Be("the-refresh-token");
        client.TokenType.Should().Be("Bearer");
        client.ExpiresAt.Should().BeCloseTo(before.AddSeconds(3600), TimeSpan.FromSeconds(30));

        userInfo.Email.Should().Be("timo@example.com");
        userInfo.FirstName.Should().Be("Timo");
        userInfo.LastName.Should().Be("Weggen");
        userInfo.ProviderName.Should().Be("MicrosoftGraph");

        // token call, then user-info call - and nothing else.
        transport.Requests.Should().HaveCount(2);
    }


    // ------------------------------------------------------------ invalid_client

    [Fact]
    public async Task InvalidClient_ExpiredSecret_SurfacesProviderMessage()
    {
        var transport = new StubOAuth2Transport()
            .RespondTo(TokenEndpointFragment, HttpStatusCode.Unauthorized, InvalidClientBody);

        var client = _createOnedriveClient(transport);

        var act = async () => await client.GetUserInfoAsync(_callbackParameters());

        var assertion = await act.Should().ThrowAsync<UnexpectedResponseException>();
        assertion.Which.Message.Should().Contain("invalid_client");
        assertion.Which.Message.Should().Contain("AADSTS7000222");
        assertion.Which.Message.Should().Contain("Unauthorized");

        // The user info endpoint must never be reached once the exchange failed.
        transport.Requests.Should().ContainSingle();
    }


    // -------------------------------------------------- MSA account without names

    [Fact]
    public async Task UserInfo_WithoutGivenNameAndSurname_ParsesAndResolvesEmail()
    {
        var transport = new StubOAuth2Transport()
            .RespondTo(TokenEndpointFragment, HttpStatusCode.OK,
                """
                {"token_type":"Bearer","expires_in":3600,"access_token":"at","refresh_token":"rt"}
                """)
            // Typical personal Microsoft account: no givenName, no surname, mail null.
            .RespondTo(UserInfoFragment, HttpStatusCode.OK,
                """
                {"@odata.context":"https://graph.microsoft.com/v1.0/$metadata#users/$entity","id":"deadbeef","businessPhones":[],"displayName":"Timo W","jobTitle":null,"mail":null,"mobilePhone":null,"officeLocation":null,"preferredLanguage":null,"userPrincipalName":"timo@outlook.com"}
                """);

        var client = _createOnedriveClient(transport);

        var userInfo = await client.GetUserInfoAsync(_callbackParameters());

        userInfo.Should().NotBeNull();
        userInfo.Id.Should().Be("deadbeef");
        // Falls back to displayName when givenName is absent.
        userInfo.FirstName.Should().Be("Timo W");
        userInfo.LastName.Should().BeEmpty();
        // "mail" is null on MSA accounts, so userPrincipalName is used.
        userInfo.Email.Should().Be("timo@outlook.com");
    }


    [Fact]
    public async Task UserInfo_WithMailPresent_PrefersMailOverUserPrincipalName()
    {
        var transport = new StubOAuth2Transport()
            .RespondTo(TokenEndpointFragment, HttpStatusCode.OK,
                """
                {"token_type":"Bearer","expires_in":3600,"access_token":"at"}
                """)
            .RespondTo(UserInfoFragment, HttpStatusCode.OK,
                """
                {"id":"1","displayName":"Timo W","mail":"real@example.com","userPrincipalName":"live.com#real@example.com"}
                """);

        var client = _createOnedriveClient(transport);

        var userInfo = await client.GetUserInfoAsync(_callbackParameters());

        userInfo.Email.Should().Be("real@example.com");
    }


    // ------------------------------------------------------- redirect uri default

    /// <summary>
    /// Acceptance #3: with <c>OAuth2:RedirectUri</c> unset the authorize URL is
    /// byte-identical to what the previously hardcoded value produced.
    /// Uses the real request factory on purpose - <c>BuildUri</c> is pure string
    /// work in RestSharp and issues no request.
    /// </summary>
    [Fact]
    public async Task GetLoginLinkUri_WithoutConfiguredRedirectUri_UsesLoopbackDefault()
    {
        var factory = new OAuth2ClientFactory(_onedriveOptions(redirectUri: null));
        var client = factory.CreateOAuth2Client(Guid.NewGuid(), "onedrive");

        var url = await client.GetLoginLinkUriAsync("some-state");

        // RestSharp 106 percent-encodes with *lower case* hex digits, so the exact
        // form emitted is "http%3a%2f%2flocalhost%3a53682%2f".
        url.Should().Contain("redirect_uri=http%3a%2f%2flocalhost%3a53682%2f");
        url.Should().Be(
            "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize"
            + "?response_type=code"
            + "&client_id=11111111-2222-3333-4444-555555555555"
            + "&redirect_uri=http%3a%2f%2flocalhost%3a53682%2f"
            + "&scope=offline_access Files.ReadWrite User.Read"
            + "&state=some-state");
    }


    [Fact]
    public async Task GetLoginLinkUri_WithConfiguredRedirectUri_UsesIt()
    {
        var factory = new OAuth2ClientFactory(
            _onedriveOptions(redirectUri: "https://backer.example.com/oauth/callback"));
        var client = factory.CreateOAuth2Client(Guid.NewGuid(), "onedrive");

        var url = await client.GetLoginLinkUriAsync("some-state");

        url.Should().Contain("redirect_uri=https%3a%2f%2fbacker.example.com%2foauth%2fcallback");
        url.Should().NotContain("53682");
    }


    [Fact]
    public void RedirectUri_BlankConfiguration_FallsBackToDefault()
    {
        var factory = new OAuth2ClientFactory(_onedriveOptions(redirectUri: "   "));
        var client = factory.CreateOAuth2Client(Guid.NewGuid(), "onedrive");

        client.Configuration.RedirectUri.Should().Be(OAuth2ClientFactory.DefaultRedirectUri);
        OAuth2ClientFactory.DefaultRedirectUri.Should().Be("http://localhost:53682/");
    }


    // -------------------------------------------------------- the seam itself

    /// <summary>
    /// Acceptance #2, part 1: prove the seam is load bearing. A request factory
    /// that refuses to hand out a transport must make the whole exchange fail -
    /// i.e. there is no code path that creates its own RestSharp client behind
    /// the seam's back.
    /// </summary>
    [Fact]
    public async Task AllTransportIsCreatedThroughTheInjectedRequestFactory()
    {
        var tripwire = Substitute.For<IRequestFactory>();
        tripwire
            .When(f => f.CreateClient())
            .Do(_ => throw new TripwireException());
        tripwire
            .When(f => f.CreateRequest())
            .Do(_ => throw new TripwireException());

        var factory = new OAuth2ClientFactory(_onedriveOptions(), () => tripwire);
        var client = factory.CreateOAuth2Client(Guid.NewGuid(), "onedrive");

        var exchange = async () => await client.GetUserInfoAsync(_callbackParameters());
        await exchange.Should().ThrowAsync<TripwireException>();

        var loginLink = async () => await client.GetLoginLinkUriAsync("state");
        await loginLink.Should().ThrowAsync<TripwireException>();
    }


    /// <summary>
    /// Acceptance #2, part 2: show what the substitute is standing in for.
    /// Without it the factory hands the client a real
    /// <see cref="RequestFactory"/> that produces a real <see cref="RestClient"/>,
    /// i.e. an actual HTTP transport pointed at login.microsoftonline.com.
    /// Asserted by type, so this check itself performs no request.
    /// </summary>
    [Fact]
    public void WithoutTheSubstitute_TheClientWouldGetARealHttpTransport()
    {
        var factory = new OAuth2ClientFactory(_onedriveOptions());
        var client = factory.CreateOAuth2Client(Guid.NewGuid(), "onedrive");

        var injected = _injectedRequestFactory(client);
        injected.Should().BeOfType<RequestFactory>();
        injected.CreateClient().Should().BeOfType<RestClient>();
    }


    [Fact]
    public void SuppliedRequestFactory_IsUsedForEveryProvider()
    {
        var transport = new StubOAuth2Transport();
        var options = new OAuthOptions
        {
            Providers = new SortedDictionary<string, OAuthProviderOptions>
            {
                ["onedrive"] = new() { ClientId = "id", ClientSecret = "secret" },
                ["dropbox"] = new() { ClientId = "id", ClientSecret = "secret" },
                ["googledrive"] = new() { ClientId = "id", ClientSecret = "secret" }
            }
        };
        var factory = new OAuth2ClientFactory(options, () => transport.Factory);

        foreach (var provider in new[] { "onedrive", "dropbox", "google", "googledrive" })
        {
            var client = factory.CreateOAuth2Client(Guid.NewGuid(), provider);
            _injectedRequestFactory(client).Should().BeSameAs(transport.Factory,
                $"provider '{provider}' must route its transport through the seam");
            client.Configuration.RedirectUri.Should().Be(OAuth2ClientFactory.DefaultRedirectUri);
        }
    }


    [Fact]
    public void UnknownProvider_Throws()
    {
        var factory = new OAuth2ClientFactory(_onedriveOptions());

        var act = () => factory.CreateOAuth2Client(Guid.NewGuid(), "aws-s3");

        act.Should().Throw<KeyNotFoundException>();
    }


    [Fact]
    public void OnUpdateOptions_ReplacesOptions_ButIgnoresNull()
    {
        var factory = new OAuth2ClientFactory(_onedriveOptions());

        factory.OnUpdateOptions(null);
        factory.CreateOAuth2Client(Guid.NewGuid(), "onedrive").Configuration.ClientId
            .Should().Be("11111111-2222-3333-4444-555555555555");

        var updated = _onedriveOptions(redirectUri: "https://updated.example/cb");
        updated.Providers["onedrive"].ClientId = "new-client-id";
        factory.OnUpdateOptions(updated);

        var client = factory.CreateOAuth2Client(Guid.NewGuid(), "onedrive");
        client.Configuration.ClientId.Should().Be("new-client-id");
        client.Configuration.RedirectUri.Should().Be("https://updated.example/cb");
    }


    // -------------------------------------------------------------------- helpers

    private static OAuth2Client _createOnedriveClient(StubOAuth2Transport transport)
    {
        var factory = new OAuth2ClientFactory(_onedriveOptions(), () => transport.Factory);
        return factory.CreateOAuth2Client(Guid.NewGuid(), "onedrive");
    }


    private static NameValueCollection _callbackParameters() => new()
    {
        { "code", "the-authorization-code" },
        { "state", Guid.NewGuid().ToString() }
    };


    private static IRequestFactory _injectedRequestFactory(OAuth2Client client)
    {
        var field = typeof(OAuth2Client).GetField(
            "_factory", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull();
        return (IRequestFactory)field!.GetValue(client)!;
    }


    private sealed class TripwireException : Exception
    {
        public TripwireException()
            : base("The transport was created outside the injected IRequestFactory.")
        {
        }
    }
}
