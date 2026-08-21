using System.Net.Sockets;
using FluentAssertions;
using Hannibal.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Hannibal.Tests.Data;

public class MigrationsCoverModelTests
{
    /// <summary>
    /// EF 9 compares the model against the migration snapshot without touching
    /// any database. Red here means a model change was made and
    /// <c>dotnet ef migrations add</c> was forgotten - a gap that would
    /// otherwise first surface as a failed production deploy.
    /// </summary>
    [Fact]
    public void EveryModelChangeHasAMigration()
    {
        var options = new DbContextOptionsBuilder<HannibalContext>()
            .UseNpgsql("Host=model-check-only;Database=model-check-only")
            .Options;

        using var context = new HannibalContext(options, NullLogger<HannibalContext>.Instance);

        context.Database.HasPendingModelChanges().Should().BeFalse(
            "every model change must ship with a migration in the same PR: "
            + "run `dotnet ef migrations add <Name> --project application/Hannibal/`");
    }
}

/// <summary>
/// The StartupMigrator retries only what retrying can fix. These pin the
/// classification of the failures it distinguishes.
/// </summary>
public class StartupMigratorClassificationTests
{
    [Fact]
    public void AMissingDatabaseIsRecognised_EvenWhenNested()
    {
        var missing = new PostgresException(
            "database \"hannibal\" does not exist", "FATAL", "FATAL",
            PostgresErrorCodes.InvalidCatalogName);

        StartupMigrator._isMissingDatabase(missing).Should().BeTrue();
        StartupMigrator._isMissingDatabase(new Exception("outer", missing)).Should().BeTrue();
        StartupMigrator._isTransient(missing).Should().BeFalse(
            "a missing database is handled by creating it, not by retrying");
    }

    [Fact]
    public void ServerNotReachableIsTransient()
    {
        var refused = new SocketException((int)SocketError.ConnectionRefused);

        StartupMigrator._isTransient(refused).Should().BeTrue();
        StartupMigrator._isTransient(new NpgsqlException("failed to connect", refused))
            .Should().BeTrue();
    }

    [Fact]
    public void ServerStillStartingIsTransient()
    {
        var starting = new PostgresException(
            "the database system is starting up", "FATAL", "FATAL",
            PostgresErrorCodes.CannotConnectNow);

        StartupMigrator._isTransient(starting).Should().BeTrue();
    }

    [Fact]
    public void ARealServerErrorIsNotRetried()
    {
        var badPassword = new PostgresException(
            "password authentication failed", "FATAL", "FATAL",
            PostgresErrorCodes.InvalidPassword);

        StartupMigrator._isTransient(badPassword).Should().BeFalse(
            "the server answered; retrying a wrong password diagnoses nothing");
        StartupMigrator._isMissingDatabase(badPassword).Should().BeFalse();
    }
}
