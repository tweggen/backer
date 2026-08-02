using System.Net.Http.Headers;

namespace Hannibal.IntegrationTests;

/// <summary>
/// Shared plumbing for the Gate C endpoint tests.
///
/// The API host is expensive to build (EF model, Identity, the whole minimal
/// API surface), so a single one is shared for the run. That is safe because
/// every test class here belongs to <see cref="PostgresCollection"/>, and xUnit
/// never runs tests of one collection concurrently. <see cref="ArrangeAsync"/>
/// returns the host to a known state before each test.
/// </summary>
[Collection(PostgresCollection.Name)]
public abstract class ApiIntegrationTestBase
{
    protected PostgresFixture Fixture { get; }

    protected BackerApiFactory Api => SharedApiHost.Get(Fixture);

    protected ApiIntegrationTestBase(PostgresFixture fixture)
    {
        Fixture = fixture;
    }


    /// <summary>
    /// Skips the test when PostgreSQL is unavailable, then resets database,
    /// recorded broadcasts and OAuth2 transport. Must be the first statement of
    /// every test, and the test must be a <c>[SkippableFact]</c>.
    /// </summary>
    protected async Task ArrangeAsync()
    {
        Fixture.SkipIfUnavailable();
        await Api.ResetAsync(Fixture);
    }


    /// <summary>
    /// An https client (so <c>UseHttpsRedirection</c> stays out of the way)
    /// carrying a bearer token for a freshly registered user.
    /// </summary>
    protected async Task<HttpClient> CreateAuthenticatedClientAsync(string email, string password)
    {
        var token = await Api.RegisterAndGetTokenAsync(email, password);

        var client = Api.CreateApiClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }


    /// <summary>
    /// A unique e-mail per test, so that Identity rows left behind by an
    /// earlier test (the fixture only truncates the Hannibal tables) can never
    /// collide with a registration.
    /// </summary>
    protected static string UniqueEmail(string prefix)
        => $"{prefix}-{Guid.NewGuid():N}@example.com";
}


/// <summary>
/// Holds the one <see cref="BackerApiFactory"/> used by the run.
///
/// It is deliberately not disposed: the collection fixture drops the throwaway
/// database with <c>WITH (FORCE)</c> after calling
/// <c>NpgsqlConnection.ClearAllPools()</c>, so a still-living host cannot block
/// the drop, and the test process exits immediately afterwards.
/// </summary>
internal static class SharedApiHost
{
    private static readonly object _lock = new();

    private static BackerApiFactory? _instance;
    private static string? _connectionString;

    public static BackerApiFactory Get(PostgresFixture fixture)
    {
        lock (_lock)
        {
            if (_instance is not null && _connectionString == fixture.ConnectionString)
            {
                return _instance;
            }

            _instance?.Dispose();
            _instance = new BackerApiFactory(fixture);
            _connectionString = fixture.ConnectionString;
            return _instance;
        }
    }
}
