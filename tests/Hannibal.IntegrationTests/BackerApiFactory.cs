using System.Net.Http.Json;
using System.Text;
using Hannibal.Configuration;
using Hannibal.Data;
using Hannibal.IntegrationTests.TestSupport;
using Hannibal.Services.Scheduling;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OAuth2.Infrastructure;

namespace Hannibal.IntegrationTests;

/// <summary>
/// Gate C: hosts the real <c>Api</c> application in-memory, wired to the
/// throwaway PostgreSQL database created by <see cref="PostgresFixture"/>.
///
/// Everything that would make the run non-deterministic, reach the network, or
/// touch the developer's real credentials is replaced here:
///
/// <list type="bullet">
/// <item>the <see cref="HannibalContext"/> registration is repointed at the
/// fixture database (belt) on top of an in-memory
/// <c>ConnectionStrings:DefaultConnection</c> (braces) - the service level
/// override is authoritative because <c>AddHannibalService</c> reads the
/// connection string at *registration* time;</item>
/// <item>the <see cref="RuleScheduler"/> <see cref="IHostedService"/> descriptor
/// is removed - with job creation enabled it writes <c>Job</c> rows on a
/// background loop;</item>
/// <item><c>Jwt:*</c> is deterministic and, more importantly, validation is
/// bound to the *runtime* <see cref="IConfiguration"/> so signing and
/// validation can never drift apart;</item>
/// <item><c>OAuth2:Providers:*</c> is overridden both in configuration and via
/// <c>PostConfigure&lt;OAuthOptions&gt;</c>, so the real user secrets loaded by
/// <c>Api/Program.cs:25</c> can never reach an OAuth2 client;</item>
/// <item><see cref="IHubContext{THub}"/> is a recording fake;</item>
/// <item>the OAuth2 RestSharp transport can be swapped per test through
/// <see cref="OAuth2Transport"/>.</item>
/// </list>
/// </summary>
public sealed class BackerApiFactory : WebApplicationFactory<Program>
{
    /*
     * Deterministic, obviously-not-production JWT settings. The key is used as
     * UTF8 bytes, so it must be at least 32 characters for HS256.
     */
    public const string JwtKey = "backer-integration-test-signing-key-0123456789";
    public const string JwtIssuer = "backer-integration-tests";
    public const string JwtAudience = "backer-integration-tests-client";

    /*
     * Stub OAuth2 credentials. These values are asserted on inside the running
     * host (see UserSecretIsolationTests) - if a real user-secret value ever
     * reached the options, that test fails.
     */
    public const string OneDriveClientId = "integration-test-onedrive-client-id";
    public const string OneDriveClientSecret = "integration-test-onedrive-client-secret";
    public const string DropboxClientId = "integration-test-dropbox-client-id";
    public const string DropboxClientSecret = "integration-test-dropbox-client-secret";
    public const string GoogleDriveClientId = "integration-test-googledrive-client-id";
    public const string GoogleDriveClientSecret = "integration-test-googledrive-client-secret";

    private readonly string _connectionString;

    /// <summary>
    /// Records every SignalR broadcast the application makes.
    /// </summary>
    public RecordingHubContext<HannibalHub> Hub { get; } = new();

    /// <summary>
    /// When set, the OAuth2 clients created by the host use this offline
    /// transport instead of a real RestSharp client. Read on every client
    /// creation, so a test may install it after the host has started.
    ///
    /// When null the real <see cref="RequestFactory"/> is used. That is needed
    /// for <c>GetLoginLinkUriAsync</c>, which only builds a URI and performs no
    /// I/O; no test drives a code-for-token exchange without installing a stub.
    /// </summary>
    public StubOAuth2Transport? OAuth2Transport { get; set; }


    public BackerApiFactory(PostgresFixture fixture)
    {
        _connectionString = fixture.ConnectionString;
    }


    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        /*
         * Not "Development": that branch of Program.cs adds Swagger, HSTS and an
         * exception handler pointing at an "/Error" endpoint which does not
         * exist, all of which only obscure test failures.
         */
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(_testConfiguration()!);
        });

        builder.ConfigureTestServices(services =>
        {
            _repointDatabase(services);
            _removeRuleSchedulerHostedService(services);
            _overrideJwtValidation(services);
            _overrideOAuth2Credentials(services);
            _overrideOAuth2ClientFactory(services);

            services.RemoveAll<IHubContext<HannibalHub>>();
            services.AddSingleton<IHubContext<HannibalHub>>(Hub);
        });
    }


    private Dictionary<string, string?> _testConfiguration() => new()
    {
        ["ConnectionStrings:DefaultConnection"] = _connectionString,

        /*
         * The fixture already ran Database.Migrate() on a freshly created
         * database, so the startup block in Program.cs has nothing to do. It is
         * skipped rather than repeated: repeating it is harmless (Migrate() is a
         * no-op and InitializeDatabaseAsync only seeds when Rules already
         * exist) but it costs a round trip and would silently mask a fixture
         * that failed to migrate.
         */
        ["Hannibal:SkipStartupMigration"] = "true",

        ["Jwt:Key"] = JwtKey,
        ["Jwt:Issuer"] = JwtIssuer,
        ["Jwt:Audience"] = JwtAudience,

        ["OAuth2:Providers:onedrive:ClientId"] = OneDriveClientId,
        ["OAuth2:Providers:onedrive:ClientSecret"] = OneDriveClientSecret,
        ["OAuth2:Providers:dropbox:ClientId"] = DropboxClientId,
        ["OAuth2:Providers:dropbox:ClientSecret"] = DropboxClientSecret,
        ["OAuth2:Providers:googledrive:ClientId"] = GoogleDriveClientId,
        ["OAuth2:Providers:googledrive:ClientSecret"] = GoogleDriveClientSecret
    };


    /// <summary>
    /// Repoints <see cref="HannibalContext"/> at the throwaway database.
    ///
    /// <c>AddHannibalService</c> resolves the connection string while it is
    /// registering the context, i.e. before any test configuration can be
    /// layered on. Rewriting the registration here is therefore the only
    /// override that is guaranteed to win, and it is what keeps the developer's
    /// live "hannibal" database out of the test run.
    /// </summary>
    private void _repointDatabase(IServiceCollection services)
    {
        var doomed = services
            .Where(_isHannibalContextRegistration)
            .ToList();

        foreach (var descriptor in doomed)
        {
            services.Remove(descriptor);
        }

        services.AddDbContext<HannibalContext>(options => options.UseNpgsql(_connectionString));
    }


    private static bool _isHannibalContextRegistration(ServiceDescriptor descriptor)
    {
        if (descriptor.ServiceType == typeof(HannibalContext)
            || descriptor.ServiceType == typeof(DbContextOptions<HannibalContext>)
            || descriptor.ServiceType == typeof(DbContextOptions))
        {
            return true;
        }

        /*
         * EF Core 9 additionally registers IDbContextOptionsConfiguration<TContext>
         * per AddDbContext call; leaving the old one in place would re-apply the
         * production connection string on top of ours.
         */
        return descriptor.ServiceType.IsGenericType
               && descriptor.ServiceType.Name.StartsWith("IDbContextOptionsConfiguration", StringComparison.Ordinal)
               && descriptor.ServiceType.GetGenericArguments()[0] == typeof(HannibalContext);
    }


    /// <summary>
    /// Removes the <see cref="RuleScheduler"/> background service. It is
    /// registered with job creation enabled and would create <c>Job</c> rows on
    /// a timer, which makes assertions non-deterministic (and, against a schema
    /// it does not like, brings the whole host down).
    ///
    /// The <c>RuleScheduler</c> singleton itself stays registered - it is also
    /// the <c>ISchedulerEventPublisher</c> that HannibalService depends on -
    /// only its ExecuteAsync loop is prevented from ever starting.
    /// </summary>
    private static void _removeRuleSchedulerHostedService(IServiceCollection services)
    {
        var doomed = services
            .Where(d => d.ServiceType == typeof(IHostedService) && _isRuleScheduler(d))
            .ToList();

        if (doomed.Count == 0)
        {
            throw new InvalidOperationException(
                "The RuleScheduler IHostedService registration was not found. Its removal is "
                + "load bearing for the integration tests; failing loudly instead of silently "
                + "running the scheduler.");
        }

        foreach (var descriptor in doomed)
        {
            services.Remove(descriptor);
        }
    }


    private static bool _isRuleScheduler(ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationType == typeof(RuleScheduler))
        {
            return true;
        }

        if (descriptor.ImplementationInstance is RuleScheduler)
        {
            return true;
        }

        /*
         * AddHostedService(sp => sp.GetRequiredService<RuleScheduler>()) keeps the
         * factory's own delegate instance, so its closed generic argument still
         * names the implementation type.
         */
        var factoryType = descriptor.ImplementationFactory?.GetType();
        return factoryType is { IsGenericType: true }
               && factoryType.GetGenericArguments().Contains(typeof(RuleScheduler));
    }


    /// <summary>
    /// Rebuilds the bearer token validation parameters from the *runtime*
    /// <see cref="IConfiguration"/>.
    ///
    /// Program.cs reads Jwt:Key while registering the authentication handler,
    /// whereas <c>TokenService</c> reads it when it mints a token. Binding
    /// validation to the same configuration instance the minting side uses makes
    /// the two sides agree by construction, whatever wins the configuration
    /// layering.
    /// </summary>
    private static void _overrideJwtValidation(IServiceCollection services)
    {
        services
            .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IConfiguration>((options, configuration) =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = false,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = configuration["Jwt:Issuer"],
                    ValidAudience = configuration["Jwt:Audience"],
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(configuration["Jwt:Key"]!))
                };
            });
    }


    /// <summary>
    /// Final say over <see cref="OAuthOptions"/>: PostConfigure runs after every
    /// configuration binding, so even if the user secrets from
    /// <c>Api/Program.cs:25</c> outranked the in-memory test configuration, the
    /// options the application actually uses hold the stub values only.
    /// </summary>
    private static void _overrideOAuth2Credentials(IServiceCollection services)
    {
        services.PostConfigure<OAuthOptions>(options =>
        {
            options.Providers.Clear();
            options.Providers["onedrive"] = new OAuthProviderOptions
            {
                ClientId = OneDriveClientId,
                ClientSecret = OneDriveClientSecret
            };
            options.Providers["dropbox"] = new OAuthProviderOptions
            {
                ClientId = DropboxClientId,
                ClientSecret = DropboxClientSecret
            };
            options.Providers["googledrive"] = new OAuthProviderOptions
            {
                ClientId = GoogleDriveClientId,
                ClientSecret = GoogleDriveClientSecret
            };

            /*
             * Left unset on purpose: the factory then falls back to
             * OAuth2ClientFactory.DefaultRedirectUri, which is the production
             * value, so the generated authorize URLs are the real ones.
             */
            options.RedirectUri = null;
        });
    }


    private void _overrideOAuth2ClientFactory(IServiceCollection services)
    {
        services.RemoveAll<IOAuth2ClientFactory>();
        services.AddSingleton<IOAuth2ClientFactory>(sp => new OAuth2ClientFactory(
            sp.GetRequiredService<IOptions<OAuthOptions>>().Value,
            () => OAuth2Transport?.Factory ?? new RequestFactory()));
    }


    /// <summary>
    /// A client that talks https so that <c>app.UseHttpsRedirection()</c> has
    /// nothing to redirect, and that does not follow redirects, so a 307 would
    /// surface as a test failure instead of being papered over.
    /// </summary>
    public HttpClient CreateApiClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false
    });


    /// <summary>
    /// Empties the mutable tables, forgets recorded broadcasts and uninstalls
    /// any OAuth2 transport stub. Every test starts from here.
    ///
    /// Before wiping anything it asserts that the <c>Jobs</c> table is still
    /// empty (Gate C acceptance 3). Because every test passes through here,
    /// this checks the *whole* preceding run, not just one test: if any test -
    /// or a background loop that survived the harness - had created a Job row,
    /// the next test fails immediately.
    /// </summary>
    public async Task ResetAsync(PostgresFixture fixture)
    {
        await using (var context = fixture.CreateContext())
        {
            var jobs = await context.Jobs.CountAsync();
            if (jobs != 0)
            {
                throw new InvalidOperationException(
                    $"{jobs} Job row(s) exist at the start of a test. No integration test may "
                    + "create jobs, and the RuleScheduler background loop must not be running.");
            }
        }

        await fixture.ResetAsync();
        Hub.Clear();
        OAuth2Transport = null;
    }


    /// <summary>
    /// Creates an Identity user through the real registration endpoint and
    /// returns a bearer token for it from the real token endpoint.
    /// </summary>
    public async Task<string> RegisterAndGetTokenAsync(string email, string password)
    {
        using var client = CreateApiClient();

        var register = await client.PostAsJsonAsync(
            "/api/auth/v1/register", new { email, password });
        if (!register.IsSuccessStatusCode)
        {
            var body = await register.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Registering {email} failed with {(int)register.StatusCode}: {body}");
        }

        var token = await client.PostAsJsonAsync(
            "/api/authb/v1/token", new LoginRequest { Email = email, Password = password });
        token.EnsureSuccessStatusCode();

        var payload = await token.Content.ReadFromJsonAsync<AccessTokenResponse>();
        return payload!.AccessToken;
    }


    /// <summary>
    /// Runs <paramref name="work"/> against a <see cref="HannibalContext"/> from
    /// the running host, i.e. the very same database the API writes to.
    /// </summary>
    public async Task<T> WithContextAsync<T>(Func<HannibalContext, Task<T>> work)
    {
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<HannibalContext>();
        return await work(context);
    }


    public async Task WithContextAsync(Func<HannibalContext, Task> work)
    {
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<HannibalContext>();
        await work(context);
    }
}
