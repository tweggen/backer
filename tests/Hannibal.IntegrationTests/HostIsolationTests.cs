using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Hannibal.Configuration;
using Hannibal.Data;
using Hannibal.Models;
using Hannibal.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Hannibal.IntegrationTests;

/// <summary>
/// Gate C acceptances 3 and 4, plus the safety property the whole gate rests
/// on: the test host must talk to the throwaway database and to nothing that
/// belongs to the developer.
/// </summary>
[Collection(PostgresCollection.Name)]
public class HostIsolationTests : ApiIntegrationTestBase
{
    public HostIsolationTests(PostgresFixture fixture) : base(fixture)
    {
    }


    /// <summary>
    /// The harness may only ever reach the <c>backer_test_*</c> database. This
    /// is asserted on the context the *application* resolves, not on
    /// configuration, because <c>AddHannibalService</c> captures the connection
    /// string while registering the context.
    /// </summary>
    [SkippableFact]
    public void The_running_host_is_bound_to_the_throwaway_database()
    {
        Fixture.SkipIfUnavailable();

        using var scope = Api.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<HannibalContext>();

        var database = new NpgsqlConnectionStringBuilder(context.Database.GetConnectionString()).Database;

        database.Should().Be(Fixture.DatabaseName);
        database.Should().StartWith("backer_test_");
        database.Should().NotBe("hannibal", "the developer's live database must never be opened");
    }


    /// <summary>
    /// Gate C acceptance 3, part one: the scheduler's background loop is gone.
    /// </summary>
    [SkippableFact]
    public void The_RuleScheduler_background_service_is_not_running()
    {
        Fixture.SkipIfUnavailable();

        var hostedServices = Api.Services.GetServices<IHostedService>().ToList();

        hostedServices.Should().NotContain(
            s => s is RuleScheduler,
            "RuleScheduler runs with job creation enabled and would write Job rows on a timer");

        // It is still resolvable as the event publisher HannibalService depends on.
        Api.Services.GetService<ISchedulerEventPublisher>().Should().NotBeNull();
    }


    /// <summary>
    /// Gate C acceptance 3, part two: with a rule that is long overdue in the
    /// database and the API under load, still no <c>Job</c> row appears.
    ///
    /// Every other test additionally re-checks this on entry - see
    /// <c>BackerApiFactory.ResetAsync</c> - so the property is asserted across
    /// the whole run, not only here.
    /// </summary>
    [SkippableFact]
    public async Task No_job_rows_are_created_even_with_an_overdue_rule_present()
    {
        await ArrangeAsync();

        await _seedOverdueRuleAsync();

        using var client = await CreateAuthenticatedClientAsync(
            UniqueEmail("job-isolation"), "Passw0rd!");

        // Exercise the API a little, then give any surviving background loop a
        // generous window in which to misbehave.
        for (var i = 0; i < 3; i++)
        {
            (await client.GetAsync("/api/hannibal/v1/rules")).StatusCode
                .Should().Be(HttpStatusCode.OK);
            (await client.GetAsync("/api/hannibal/v1/jobs")).StatusCode
                .Should().Be(HttpStatusCode.OK);
        }

        await Task.Delay(TimeSpan.FromSeconds(3));

        await using var context = Fixture.CreateContext();
        (await context.Rules.CountAsync()).Should().Be(1, "the overdue rule is really there");
        (await context.Jobs.CountAsync()).Should().Be(0);
        (await context.RuleStates.CountAsync()).Should().Be(0);

        var jobs = await client.GetFromJsonAsync<List<Job>>("/api/hannibal/v1/jobs");
        jobs.Should().BeEmpty();

        /*
         * ResetAsync would trip over the leftover rule for the next test, so
         * clean up here rather than relying on ordering.
         */
        await Fixture.ResetAsync();
    }


    /// <summary>
    /// Gate C acceptance 4. Established two ways: the configuration layer the
    /// host runs on holds the stub values (so the in-memory test configuration
    /// really does outrank the user secrets that <c>Api/Program.cs:25</c>
    /// loads), and the <see cref="OAuthOptions"/> the application actually
    /// builds its OAuth2 clients from holds them too.
    ///
    /// <see cref="TriggerOAuth2EndpointTests"/> closes the loop by asserting the
    /// stub client id in the authorize URL that leaves the process.
    /// </summary>
    [SkippableFact]
    public void No_real_user_secret_reaches_the_oauth2_configuration()
    {
        Fixture.SkipIfUnavailable();

        var configuration = Api.Services.GetRequiredService<IConfiguration>();

        configuration["OAuth2:Providers:onedrive:ClientId"]
            .Should().Be(BackerApiFactory.OneDriveClientId);
        configuration["OAuth2:Providers:onedrive:ClientSecret"]
            .Should().Be(BackerApiFactory.OneDriveClientSecret);
        configuration["OAuth2:Providers:dropbox:ClientId"]
            .Should().Be(BackerApiFactory.DropboxClientId);
        configuration["OAuth2:Providers:dropbox:ClientSecret"]
            .Should().Be(BackerApiFactory.DropboxClientSecret);

        var options = Api.Services.GetRequiredService<IOptions<OAuthOptions>>().Value;

        options.Providers.Keys.Should().BeEquivalentTo(new[] { "onedrive", "dropbox", "googledrive" });

        options.Providers["onedrive"].ClientId.Should().Be(BackerApiFactory.OneDriveClientId);
        options.Providers["onedrive"].ClientSecret.Should().Be(BackerApiFactory.OneDriveClientSecret);
        options.Providers["dropbox"].ClientId.Should().Be(BackerApiFactory.DropboxClientId);
        options.Providers["dropbox"].ClientSecret.Should().Be(BackerApiFactory.DropboxClientSecret);
        options.Providers["googledrive"].ClientId.Should().Be(BackerApiFactory.GoogleDriveClientId);
        options.Providers["googledrive"].ClientSecret.Should().Be(BackerApiFactory.GoogleDriveClientSecret);

        /*
         * Every stub value is recognisable; none of them can be a real
         * credential, and the placeholders from Api/appsettings.json are gone
         * as well.
         */
        foreach (var provider in options.Providers.Values)
        {
            provider.ClientId.Should().StartWith("integration-test-");
            provider.ClientSecret.Should().StartWith("integration-test-");
        }
    }


    /// <summary>
    /// A rule whose destination is a year stale: exactly the input the scheduler
    /// would turn into a job the moment its loop ran.
    /// </summary>
    private async Task _seedOverdueRuleAsync()
    {
        await using var context = Fixture.CreateContext();

        var source = new Storage
        {
            UserId = "job-isolation", Technology = "smb",
            UriSchema = "JobIsolationSource", IsActive = true
        };
        var destination = new Storage
        {
            UserId = "job-isolation", Technology = "onedrive",
            UriSchema = "JobIsolationDestination", IsActive = true
        };
        context.Storages.AddRange(source, destination);

        var sourceEndpoint = new Endpoint
        {
            Name = "job-isolation-source", UserId = "job-isolation",
            Storage = source, Path = "/source", IsActive = true
        };
        var destinationEndpoint = new Endpoint
        {
            Name = "job-isolation-destination", UserId = "job-isolation",
            Storage = destination, Path = "/destination", IsActive = true
        };
        context.Endpoints.AddRange(sourceEndpoint, destinationEndpoint);

        context.Rules.Add(new Rule
        {
            Name = "job-isolation-rule",
            Comment = "must never produce a job during the integration run",
            UserId = "job-isolation",
            SourceEndpoint = sourceEndpoint,
            DestinationEndpoint = destinationEndpoint,
            Operation = Rule.RuleOperation.Copy,
            MaxDestinationAge = TimeSpan.FromMinutes(1),
            MinRetryTime = TimeSpan.FromMinutes(1),
            MaxTimeAfterSourceModification = TimeSpan.FromMinutes(1),
            DailyTriggerTime = TimeSpan.FromHours(1)
        });

        await context.SaveChangesAsync();
    }
}
