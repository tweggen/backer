using System.Net;
using FluentAssertions;
using Hannibal.Configuration;
using Hannibal.Data;
using Hannibal.Models;
using Hannibal.Services;
using Hannibal.Tests.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Hannibal.Tests.Services;

/// <summary>
/// Gate D acceptance #4: <c>ProcessOAuth2ResultAsync</c> must *log* a failed
/// code-for-token exchange. These tests drive the real
/// <see cref="HannibalService"/> with a capturing logger, so a regression back
/// to a silent failure breaks the build.
///
/// No database is involved: on the failure path the service never touches
/// <see cref="HannibalContext"/>, and the context is deliberately configured
/// with an unusable connection string so that any accidental query would fail
/// loudly rather than reach a real server.
/// </summary>
public class ProcessOAuth2ResultLoggingTests
{
    private const string InvalidClientBody =
        """
        {"error":"invalid_client","error_description":"AADSTS7000222: The provided client secret keys for app '11111111-2222-3333-4444-555555555555' are expired. Visit the Azure portal to create new keys for your app: https://aka.ms/NewClientSecret.","error_codes":[7000222],"timestamp":"2026-07-14 08:12:33Z","trace_id":"a0a0a0a0-b1b1-c2c2-d3d3-e4e4e4e4e4e4","correlation_id":"f5f5f5f5-0606-1717-2828-393939393939"}
        """;


    [Fact]
    public async Task InvalidClient_IsLoggedAsErrorAndReportedToTheCaller()
    {
        var transport = new StubOAuth2Transport()
            .RespondTo("/token", HttpStatusCode.Unauthorized, InvalidClientBody);

        var stateEntry = new OAuthState
        {
            UserId = "timo@example.com",
            Provider = "onedrive",
            ReturnUrl = "https://backer.example.com/storages"
        };

        var harness = new Harness(transport, stateEntry);

        var result = await harness.Service.ProcessOAuth2ResultAsync(
            new DefaultHttpContext().Request,
            code: "the-authorization-code",
            state: stateEntry.Id.ToString(),
            error: null,
            errorDescription: null,
            CancellationToken.None);

        // The caller sees the provider's own diagnosis, not a bare failure.
        result.Error.Should().Contain("invalid_client");
        result.Error.Should().Contain("AADSTS7000222");
        result.AfterAuthUri.Should().Be("https://backer.example.com/storages");

        // ... and it was logged, with the exception attached.
        var errors = harness.Logger.Entries
            .Where(e => e.Level == LogLevel.Error)
            .ToList();

        errors.Should().ContainSingle(
            "a failed OAuth2 exchange must never be swallowed silently");
        errors[0].Exception.Should().NotBeNull();
        errors[0].Exception!.Message.Should().Contain("AADSTS7000222");
        errors[0].Message.Should().Contain("onedrive");
        errors[0].Message.Should().Contain(stateEntry.Id.ToString());
        errors[0].Message.Should().Contain("timo@example.com");
        errors[0].Message.Should().Contain("invalid_client");

        // The exchange never got as far as the user-info endpoint.
        transport.Requests.Should().ContainSingle();
    }


    [Fact]
    public async Task ProviderReportedError_IsEchoedBackWithoutContactingTheProvider()
    {
        var transport = new StubOAuth2Transport();
        var harness = new Harness(transport, new OAuthState
        {
            UserId = "timo@example.com",
            Provider = "onedrive",
            ReturnUrl = "https://backer.example.com/storages"
        });

        var httpRequest = new DefaultHttpContext().Request;
        httpRequest.QueryString = new QueryString(
            "?error=access_denied&error_description=The+user+denied+the+request");

        var result = await harness.Service.ProcessOAuth2ResultAsync(
            httpRequest,
            code: null,
            state: null,
            error: "access_denied",
            errorDescription: "The user denied the request",
            CancellationToken.None);

        result.Error.Should().Be("access_denied");
        result.ErrorDescription.Should().Be("The user denied the request");
        transport.Requests.Should().BeEmpty();
    }


    [Fact]
    public async Task UnknownState_IsRejected()
    {
        var harness = new Harness(new StubOAuth2Transport(), stateEntry: null);

        var act = async () => await harness.Service.ProcessOAuth2ResultAsync(
            new DefaultHttpContext().Request,
            code: "code",
            state: Guid.NewGuid().ToString(),
            error: null,
            errorDescription: null,
            CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("State not found");
    }


    /// <summary>
    /// Builds a <see cref="HannibalService"/> whose only live collaborator is the
    /// OAuth2 client factory sitting on the stubbed transport.
    /// </summary>
    private sealed class Harness
    {
        public CapturingLogger<HannibalService> Logger { get; } = new();
        public HannibalService Service { get; }

        public Harness(StubOAuth2Transport transport, OAuthState? stateEntry)
        {
            var oauthOptions = new OAuthOptions
            {
                Providers = new SortedDictionary<string, OAuthProviderOptions>
                {
                    ["onedrive"] = new()
                    {
                        ClientId = "11111111-2222-3333-4444-555555555555",
                        ClientSecret = "an-expired-secret"
                    }
                }
            };

            var stateService = Substitute.For<IOAuthStateService>();
            stateService
                .ValidateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(stateEntry);

            var userManager = Substitute.For<UserManager<IdentityUser>>(
                Substitute.For<IUserStore<IdentityUser>>(),
                null, null, null, null, null, null, null, null);

            Service = new HannibalService(
                _createNeverConnectedContext(),
                Logger,
                Options.Create(new HannibalServiceOptions()),
                Options.Create(oauthOptions),
                Substitute.For<IHubContext<HannibalHub>>(),
                userManager,
                stateService,
                Substitute.For<IHttpContextAccessor>(),
                Substitute.For<IServiceProvider>(),
                new OAuth2ClientFactory(oauthOptions, () => transport.Factory));
        }

        /// <summary>
        /// A context that is structurally valid but points at a port nothing
        /// listens on. Constructing it opens no connection; using it would fail
        /// immediately instead of reaching any real database.
        /// </summary>
        private static HannibalContext _createNeverConnectedContext()
        {
            var options = new DbContextOptionsBuilder<HannibalContext>()
                .UseNpgsql("Host=127.0.0.1;Port=1;Database=hannibal_tests_never_connected;"
                           + "Username=none;Password=none;Timeout=1")
                .Options;
            return new HannibalContext(options, NullLogger<HannibalContext>.Instance);
        }
    }
}
