using FluentAssertions;
using Hannibal.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TestSupport.Api;
using Xunit;

namespace Hannibal.IntegrationTests;

/// <summary>
/// Gate M1 of <c>docs/plan-db-migration-strategy.md</c>: the startup migrator
/// creates and migrates a fresh database, refuses a database poisoned by
/// <c>EnsureCreated</c> with the runbook error instead of a raw
/// "relation already exists", and gives up on an unreachable server with a
/// bounded, explained timeout.
///
/// The fresh/poisoned tests create their own uniquely named sibling databases
/// via the fixture's admin connection - never the fixture's shared database,
/// whose schema other tests rely on.
/// </summary>
[Collection(PostgresCollection.Name)]
public class StartupMigratorTests
{
    private readonly PostgresFixture _fixture;

    public StartupMigratorTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task AFreshDatabase_IsCreatedAndFullyMigrated()
    {
        _fixture.SkipIfUnavailable();
        var databaseName = _uniqueDatabaseName();

        try
        {
            await using var context = _contextFor(databaseName);

            await StartupMigrator.MigrateAsync(context, NullLogger.Instance);

            var applied = (await context.Database.GetAppliedMigrationsAsync()).ToList();
            var known = context.Database.GetMigrations().ToList();
            applied.Should().Equal(known,
                "a fresh database must come out with every known migration applied");
        }
        finally
        {
            await _dropDatabaseAsync(databaseName);
        }
    }

    [SkippableFact]
    public async Task ADatabaseWithoutMigrationHistory_FailsWithTheRunbookError()
    {
        _fixture.SkipIfUnavailable();
        var databaseName = _uniqueDatabaseName();

        try
        {
            /*
             * Build exactly the poisoned state the old startup path produced:
             * EnsureCreated makes the full schema but no __EFMigrationsHistory.
             */
            await using (var poisoned = _contextFor(databaseName))
            {
                await poisoned.Database.EnsureCreatedAsync();
            }

            await using var context = _contextFor(databaseName);
            var migrate = () => StartupMigrator.MigrateAsync(context, NullLogger.Instance);

            (await migrate.Should().ThrowAsync<InvalidOperationException>(
                    "migrating an EnsureCreated database would die on "
                    + "\"relation already exists\", which diagnoses nothing"))
                .WithMessage("*plan-db-migration-strategy*");
        }
        finally
        {
            await _dropDatabaseAsync(databaseName);
        }
    }

    /// <summary>
    /// Needs no PostgreSQL: the point is what happens when there is none.
    /// Port 9 (discard) refuses on any sane machine; the tight timeout keeps
    /// the test fast while still proving the deadline is honoured.
    /// </summary>
    [Fact]
    public async Task AnUnreachableServer_FailsWithABoundedTimeout()
    {
        var options = new DbContextOptionsBuilder<HannibalContext>()
            .UseNpgsql("Host=127.0.0.1;Port=9;Database=nowhere;"
                       + "Username=nobody;Password=nothing;Timeout=1")
            .Options;
        await using var context = new HannibalContext(options, NullLogger<HannibalContext>.Instance);

        var migrate = () => StartupMigrator.MigrateAsync(
            context, NullLogger.Instance, timeout: TimeSpan.FromSeconds(2));

        (await migrate.Should().ThrowAsync<TimeoutException>())
            .WithMessage("*not reachable*");
    }


    private string _uniqueDatabaseName()
        => $"backer_migrator_{Guid.NewGuid():N}"[..40];

    private HannibalContext _contextFor(string databaseName)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(_fixture.AdminConnectionString)
        {
            Database = databaseName
        }.ConnectionString;

        var options = new DbContextOptionsBuilder<HannibalContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new HannibalContext(options, NullLogger<HannibalContext>.Instance);
    }

    private async Task _dropDatabaseAsync(string databaseName)
    {
        NpgsqlConnection.ClearAllPools();

        await using var admin = new NpgsqlConnection(_fixture.AdminConnectionString);
        await admin.OpenAsync();

        await using var command = admin.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }
}
