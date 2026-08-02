using FluentAssertions;
using Hannibal.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Hannibal.IntegrationTests;

/// <summary>
/// Gate B acceptance tests for the throwaway PostgreSQL fixture itself.
/// </summary>
[Collection(PostgresCollection.Name)]
public class PostgresFixtureTests
{
    private readonly PostgresFixture _fixture;

    public PostgresFixtureTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }


    /// <summary>
    /// Gate B acceptance 1: a Storage written through one context is readable,
    /// unchanged, through a second, independent context.
    /// </summary>
    [SkippableFact]
    public async Task Storage_written_through_one_context_is_read_back_by_another()
    {
        _fixture.SkipIfUnavailable();
        await _fixture.ResetAsync();

        var written = new Storage
        {
            UserId = "gate-b-user",
            Technology = "onedrive",
            UriSchema = "GateBOneDrive",
            Networks = "",
            OAuth2Email = "gate-b@example.com",
            ClientId = "client-id-b",
            ClientSecret = "client-secret-b",
            AccessToken = "access-token-b",
            RefreshToken = "refresh-token-b",
            ExpiresAt = new DateTime(2026, 8, 2, 10, 0, 0, DateTimeKind.Utc),
            Username = "user-b",
            Password = "password-b",
            Host = "host-b",
            Port = 445,
            Domain = "domain-b",
            IsActive = true
        };

        await using (var writeContext = _fixture.CreateContext())
        {
            writeContext.Storages.Add(written);
            await writeContext.SaveChangesAsync();
        }

        written.Id.Should().BeGreaterThan(0, "the database must have assigned an identity");

        await using var readContext = _fixture.CreateContext();
        var read = await readContext.Storages.SingleAsync(s => s.Id == written.Id);

        read.Should().NotBeSameAs(written, "the row must come from a genuinely separate context");
        read.Should().BeEquivalentTo(
            written,
            options => options.Excluding(s => s.CreatedAt).Excluding(s => s.UpdatedAt));

        read.CreatedAt.Should().BeCloseTo(written.CreatedAt, TimeSpan.FromMilliseconds(1));
        read.UpdatedAt.Should().BeCloseTo(written.UpdatedAt, TimeSpan.FromMilliseconds(1));
    }


    /// <summary>
    /// Gate B acceptance 2: all migrations apply cleanly to an empty database.
    /// </summary>
    [SkippableFact]
    public async Task All_migrations_apply_cleanly_and_leave_nothing_pending()
    {
        _fixture.SkipIfUnavailable();

        await using var context = _fixture.CreateContext();

        var pending = await context.Database.GetPendingMigrationsAsync();
        pending.Should().BeEmpty();

        _fixture.AppliedMigrations.Should().HaveCount(6);
        _fixture.AppliedMigrations.Should().Contain("20260120100000_AddCredentialFieldsToStorage");
    }


    /// <summary>
    /// Gate B acceptance 5: the fixture works against its own database, never
    /// the developer's live "hannibal" one.
    /// </summary>
    [SkippableFact]
    public void Fixture_database_is_a_throwaway_and_not_the_live_database()
    {
        _fixture.SkipIfUnavailable();

        _fixture.DatabaseName.Should().StartWith("backer_test_");
        _fixture.DatabaseName.Should().NotBe("hannibal");
        _fixture.DatabaseName.Should().MatchRegex(@"^backer_test_\d{14}_[0-9a-f]{8}$");

        new NpgsqlConnectionStringBuilder(_fixture.ConnectionString).Database
            .Should().Be(_fixture.DatabaseName);
    }


    /// <summary>
    /// Per-test isolation: ResetAsync empties the mutable tables so nothing
    /// leaks between tests.
    /// </summary>
    [SkippableFact]
    public async Task ResetAsync_removes_rows_written_by_a_previous_test()
    {
        _fixture.SkipIfUnavailable();
        await _fixture.ResetAsync();

        await using (var seedContext = _fixture.CreateContext())
        {
            var storage = new Storage
            {
                UserId = "isolation-user",
                Technology = "dropbox",
                UriSchema = "IsolationDropbox"
            };
            seedContext.Storages.Add(storage);

            seedContext.Endpoints.Add(new Endpoint
            {
                Name = "isolation-endpoint",
                UserId = "isolation-user",
                Storage = storage,
                Path = "/tmp",
                Comment = "isolation endpoint",
                IsActive = true
            });
            await seedContext.SaveChangesAsync();
        }

        await using (var beforeContext = _fixture.CreateContext())
        {
            (await beforeContext.Storages.CountAsync()).Should().BeGreaterThan(0);
            (await beforeContext.Endpoints.CountAsync()).Should().BeGreaterThan(0);
        }

        await _fixture.ResetAsync();

        await using var afterContext = _fixture.CreateContext();
        (await afterContext.Storages.CountAsync()).Should().Be(0);
        (await afterContext.Endpoints.CountAsync()).Should().Be(0);
        (await afterContext.Jobs.CountAsync()).Should().Be(0);
        (await afterContext.Rules.CountAsync()).Should().Be(0);
        (await afterContext.RuleStates.CountAsync()).Should().Be(0);
        (await afterContext.OAuthStates.CountAsync()).Should().Be(0);
    }
}
