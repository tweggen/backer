using Hannibal.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Hannibal.IntegrationTests;

/// <summary>
/// Creates a throwaway PostgreSQL database for the duration of one test run,
/// applies every EF Core migration to it, and drops it again on dispose.
///
/// The fixture never touches the developer's real <c>hannibal</c> database: it
/// connects to the <c>postgres</c> maintenance database only to issue
/// CREATE DATABASE / DROP DATABASE for its own uniquely named database.
///
/// If PostgreSQL cannot be reached the fixture does NOT throw. It records the
/// failure in <see cref="SkipReason"/> so that every test in the collection is
/// reported as *skipped* rather than failed.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>
    /// Environment variable holding the admin ("maintenance") connection string.
    /// </summary>
    public const string ConnectionEnvironmentVariable = "BACKER_TEST_DB_CONNECTION";

    public const string DefaultAdminConnectionString =
        "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=admin";

    /// <summary>
    /// Tables that integration tests are allowed to dirty, in an order that is
    /// safe to truncate. TRUNCATE ... CASCADE makes the order irrelevant, but it
    /// is kept child-before-parent for readability.
    /// </summary>
    private static readonly string[] _mutableTables =
    {
        "\"RuleStates\"",
        "\"Jobs\"",
        "\"Rules\"",
        "\"Endpoints\"",
        "\"Storages\"",
        "\"oauth_state\""
    };

    /// <summary>
    /// Connection string used to create and drop the throwaway database.
    /// </summary>
    public string AdminConnectionString { get; }

    /// <summary>
    /// Name of the throwaway database, unique per run so that parallel or
    /// aborted runs can never collide.
    /// </summary>
    public string DatabaseName { get; }

    /// <summary>
    /// Connection string pointing at <see cref="DatabaseName"/>. Only meaningful
    /// once <see cref="IsAvailable"/> is true.
    /// </summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// Host/port/database of the admin connection. Safe to print: it never
    /// contains the password.
    /// </summary>
    public string ConnectionTarget { get; private set; } = "<unparsed connection string>";

    /// <summary>
    /// False when PostgreSQL could not be reached; tests must then skip.
    /// </summary>
    public bool IsAvailable { get; private set; }

    /// <summary>
    /// Human readable reason naming the connection target, set when
    /// <see cref="IsAvailable"/> is false.
    /// </summary>
    public string? SkipReason { get; private set; }

    /// <summary>
    /// Migrations that were applied to the throwaway database.
    /// </summary>
    public IReadOnlyList<string> AppliedMigrations { get; private set; } = Array.Empty<string>();

    private bool _databaseCreated;

    public PostgresFixture()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        AdminConnectionString = string.IsNullOrWhiteSpace(fromEnvironment)
            ? DefaultAdminConnectionString
            : fromEnvironment;

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        DatabaseName = $"backer_test_{DateTime.UtcNow:yyyyMMddHHmmss}_{suffix}";
    }


    public async Task InitializeAsync()
    {
        /*
         * Step 1 - connectivity. Any failure here means "no PostgreSQL", which
         * is a skip, not a test failure.
         */
        try
        {
            var adminBuilder = new NpgsqlConnectionStringBuilder(AdminConnectionString)
            {
                Timeout = 10
            };
            ConnectionTarget = $"{adminBuilder.Host}:{adminBuilder.Port}/{adminBuilder.Database}";

            ConnectionString = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
            {
                Database = DatabaseName
            }.ConnectionString;

            await using var probe = new NpgsqlConnection(adminBuilder.ConnectionString);
            await probe.OpenAsync();
        }
        catch (Exception e)
        {
            IsAvailable = false;
            SkipReason =
                $"PostgreSQL is not reachable at {ConnectionTarget} "
                + $"(set {ConnectionEnvironmentVariable} to point at a reachable server). "
                + $"{e.GetType().Name}: {e.Message}";
            return;
        }

        /*
         * Step 2 - schema. From here on a failure is a genuine failure: the
         * server answered, so a broken migration must not be hidden as a skip.
         */
        await _createDatabaseAsync();

        await using (var context = CreateContext())
        {
            await context.Database.MigrateAsync();

            var pending = (await context.Database.GetPendingMigrationsAsync()).ToList();
            if (pending.Count > 0)
            {
                throw new InvalidOperationException(
                    $"After Migrate() the database {DatabaseName} still reports "
                    + $"{pending.Count} pending migration(s): {string.Join(", ", pending)}");
            }

            AppliedMigrations = (await context.Database.GetAppliedMigrationsAsync()).ToList();
        }

        IsAvailable = true;
    }


    public async Task DisposeAsync()
    {
        /*
         * Pooled connections would otherwise keep the database busy. FORCE
         * additionally terminates anything still attached.
         */
        NpgsqlConnection.ClearAllPools();

        if (!_databaseCreated)
        {
            return;
        }

        await using var admin = new NpgsqlConnection(AdminConnectionString);
        await admin.OpenAsync();

        await using var command = admin.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();

        _databaseCreated = false;
    }


    /// <summary>
    /// Creates a fresh context against the throwaway database. The caller owns
    /// the returned instance and must dispose it.
    /// </summary>
    public HannibalContext CreateContext()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            throw new InvalidOperationException(
                "The PostgreSQL fixture is unavailable; call SkipIfUnavailable() first.");
        }

        var options = new DbContextOptionsBuilder<HannibalContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new HannibalContext(options, NullLogger<HannibalContext>.Instance);
    }


    /// <summary>
    /// Empties every table an integration test may write to, so that tests do
    /// not leak state into one another.
    /// </summary>
    public async Task ResetAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"TRUNCATE TABLE {string.Join(", ", _mutableTables)} RESTART IDENTITY CASCADE";
        await command.ExecuteNonQueryAsync();
    }


    /// <summary>
    /// Aborts the calling test with a *skipped* result when PostgreSQL is not
    /// available. Requires the calling test to be a [SkippableFact].
    /// </summary>
    public void SkipIfUnavailable()
    {
        Skip.IfNot(IsAvailable, SkipReason ?? $"PostgreSQL is not reachable at {ConnectionTarget}.");
    }


    private async Task _createDatabaseAsync()
    {
        await using var admin = new NpgsqlConnection(AdminConnectionString);
        await admin.OpenAsync();

        await using var command = admin.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{DatabaseName}\"";
        await command.ExecuteNonQueryAsync();

        _databaseCreated = true;
    }
}


/// <summary>
/// Every test class carrying [Collection(PostgresCollection.Name)] shares one
/// throwaway database and therefore one CREATE/DROP cycle per run.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "PostgreSQL integration";
}
