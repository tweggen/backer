using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Hannibal.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hannibal.IntegrationTests;

/// <summary>
/// Gate E AC8 (plan-git-repo-storage.md): a reported DoneFailure with
/// <c>JobStatus.Terminal = true</c> (the git engine's signal that a §5 safety
/// guard tripped, not an ordinary transient failure) must not be requeued to
/// Ready for an immediate re-acquire - it is recorded as-is, DoneFailure with
/// Owner cleared, so RuleScheduler schedules the rule's next attempt per
/// <c>ScheduleCalculator</c>'s LastReported + MinRetryTime path
/// (ScheduleCalculator.cs:56-62) instead of the job spinning through
/// acquisition again seconds later. The contrasting non-terminal case (an
/// ordinary failure, Terminal defaulting to false - what every pre-Gate-E
/// agent still sends) keeps today's retry-by-requeue behaviour unchanged.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ReportJobTerminalFailureTests : ApiIntegrationTestBase
{
    private const string Password = "Passw0rd!";
    private const string ReportRoute = "/api/hannibal/v1/reportJob";

    public ReportJobTerminalFailureTests(PostgresFixture fixture) : base(fixture)
    {
    }

    [SkippableFact]
    public async Task Terminal_DoneFailure_is_recorded_as_is_and_not_requeued_to_Ready()
    {
        await ArrangeAsync();
        var email = UniqueEmail("terminal-failure");
        using var client = await CreateAuthenticatedClientAsync(email, Password);
        var userId = await _userIdAsync(email);

        var jobId = await _seedExecutingJobAsync(userId, "terminal-guard-tripped", owner: "git-agent-1");

        var report = await client.PostAsJsonAsync(ReportRoute, new JobStatus
        {
            JobId = jobId, Owner = "git-agent-1", State = Job.JobState.DoneFailure, Terminal = true
        });
        report.StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var context = Fixture.CreateContext())
        {
            var row = await context.Jobs.AsNoTracking().FirstAsync(j => j.Id == jobId);
            row.State.Should().Be(Job.JobState.DoneFailure,
                "a guard-tripped failure must stay terminal, not be requeued");
            row.State.Should().NotBe(Job.JobState.Ready);
            row.Owner.Should().Be("");
        }

        await Fixture.ResetAsync();
    }

    [SkippableFact]
    public async Task NonTerminal_DoneFailure_is_requeued_to_Ready_for_retry()
    {
        await ArrangeAsync();
        var email = UniqueEmail("nonterminal-failure");
        using var client = await CreateAuthenticatedClientAsync(email, Password);
        var userId = await _userIdAsync(email);

        var jobId = await _seedExecutingJobAsync(userId, "ordinary-failure", owner: "agent-1");

        var report = await client.PostAsJsonAsync(ReportRoute, new JobStatus
        {
            JobId = jobId, Owner = "agent-1", State = Job.JobState.DoneFailure, Terminal = false
        });
        report.StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var context = Fixture.CreateContext())
        {
            var row = await context.Jobs.AsNoTracking().FirstAsync(j => j.Id == jobId);
            row.State.Should().Be(Job.JobState.Ready,
                "an ordinary (non-guard) failure keeps today's retry-by-requeue behaviour");
            row.Owner.Should().Be("");
        }

        await Fixture.ResetAsync();
    }

    [SkippableFact]
    public async Task Omitting_Terminal_defaults_to_false_and_is_requeued_JustLikeAnOldAgent()
    {
        await ArrangeAsync();
        var email = UniqueEmail("legacy-agent-failure");
        using var client = await CreateAuthenticatedClientAsync(email, Password);
        var userId = await _userIdAsync(email);

        var jobId = await _seedExecutingJobAsync(userId, "legacy-agent-failure", owner: "old-agent");

        // No Terminal field at all - the wire shape a pre-Gate-E agent sends.
        var payload = new { JobId = jobId, Owner = "old-agent", State = Job.JobState.DoneFailure };
        var report = await client.PostAsJsonAsync(ReportRoute, payload);
        report.StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var context = Fixture.CreateContext())
        {
            var row = await context.Jobs.AsNoTracking().FirstAsync(j => j.Id == jobId);
            row.State.Should().Be(Job.JobState.Ready,
                "an old agent that never sends Terminal must keep the pre-Gate-E retry behaviour");
            row.Owner.Should().Be("");
        }

        await Fixture.ResetAsync();
    }

    private async Task<int> _seedExecutingJobAsync(string userId, string tagPrefix, string owner)
    {
        await using var context = Fixture.CreateContext();

        var source = new Storage
        {
            UserId = userId, Technology = "git", UriSchema = $"{tagPrefix}-src-{Guid.NewGuid():N}",
            Host = "https://github.com/", IsActive = true
        };
        var destination = new Storage
        {
            UserId = userId, Technology = "git", UriSchema = $"{tagPrefix}-dst-{Guid.NewGuid():N}",
            Host = "https://codeberg.org/", IsActive = true
        };
        context.Storages.AddRange(source, destination);

        var sourceEndpoint = new Endpoint
        {
            Name = $"{tagPrefix}-src-ep-{Guid.NewGuid():N}", UserId = userId, Storage = source,
            Path = "owner/repo-one", IsActive = true
        };
        var destinationEndpoint = new Endpoint
        {
            Name = $"{tagPrefix}-dst-ep-{Guid.NewGuid():N}", UserId = userId, Storage = destination,
            Path = "owner/repo-two", IsActive = true
        };
        context.Endpoints.AddRange(sourceEndpoint, destinationEndpoint);

        var rule = new Rule
        {
            Name = $"{tagPrefix}-rule-{Guid.NewGuid():N}", Comment = "", UserId = userId,
            SourceEndpoint = sourceEndpoint, DestinationEndpoint = destinationEndpoint,
            Operation = Rule.RuleOperation.Sync, MinRetryTime = TimeSpan.FromMinutes(15)
        };

        var now = DateTime.UtcNow;
        var job = new Job
        {
            UserId = userId, Tag = tagPrefix, Operation = Rule.RuleOperation.Sync,
            FromRule = rule, Owner = owner, State = Job.JobState.Executing,
            StartFrom = now.AddMinutes(-5), EndBy = now.AddDays(1), LastReported = now,
            SourceEndpoint = sourceEndpoint, DestinationEndpoint = destinationEndpoint
        };
        context.Jobs.Add(job);
        await context.SaveChangesAsync();
        return job.Id;
    }

    private async Task<string> _userIdAsync(string email)
    {
        using var scope = Api.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByEmailAsync(email);
        user.Should().NotBeNull();
        return user!.Id;
    }
}
