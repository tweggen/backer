using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Hannibal.Data;
using Hannibal.Models;
using Microsoft.EntityFrameworkCore;

namespace Hannibal.IntegrationTests;

/// <summary>
/// Gate C (docs/plan-git-repo-storage.md): job acquisition becomes
/// capability-aware. Rows are seeded directly through the DbContext rather
/// than through the rule-creation API - Gate B rejects git+git rules by
/// design, but a Job can still be seeded to exercise the acquisition filter
/// ahead of Gate D shipping the git engine itself.
/// </summary>
[Collection(PostgresCollection.Name)]
public class JobAcquisitionCapabilityTests : ApiIntegrationTestBase
{
    private const string Password = "Passw0rd!";
    private const string AcquireRoute = "/api/hannibal/v1/acquireNextJob";

    public JobAcquisitionCapabilityTests(PostgresFixture fixture) : base(fixture)
    {
    }

    /// <summary>
    /// AC1: an agent advertising "rclone" receives only the rclone job and
    /// one advertising "git" only the git job. Run with the git job seeded
    /// both after and before the rclone job (different id and StartFrom
    /// ordering) to prove the result comes from engine classification, not
    /// from insertion order or <c>StartFrom</c>.
    /// </summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AC1_each_agent_receives_only_the_job_matching_its_engine(bool gitJobSeededFirst)
    {
        await ArrangeAsync();
        using var client = await CreateAuthenticatedClientAsync(UniqueEmail("engine-filter"), Password);

        var seed = await _seedRcloneAndGitJobsAsync(gitJobSeededFirst);

        var rcloneResponse = await _acquireAsync(client, "rclone");
        rcloneResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var rcloneJob = await rcloneResponse.Content.ReadFromJsonAsync<Job>();
        rcloneJob.Should().NotBeNull();
        rcloneJob!.Id.Should().Be(seed.RcloneJobId);

        var gitResponse = await _acquireAsync(client, "git");
        gitResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var gitJob = await gitResponse.Content.ReadFromJsonAsync<Job>();
        gitJob.Should().NotBeNull();
        gitJob!.Id.Should().Be(seed.GitJobId);

        /*
         * ResetAsync asserts the Jobs table is empty at the start of the
         * next test (Gate C acceptance 3 in BackerApiFactory.ResetAsync) -
         * clean up the jobs this test seeded rather than relying on ordering.
         */
        await Fixture.ResetAsync();
    }

    /// <summary>
    /// AC2: the legacy "use_me" sentinel and an empty string both fall back
    /// to rclone-only, so the git job stays untouched. (null cannot be
    /// represented as a JSON string body field the same way over REST here -
    /// that case is covered at the parser unit level in
    /// AgentCapabilitiesTests.Parse_LegacyOrUnrecognizedInput_FallsBackToRcloneOnly.)
    /// </summary>
    [SkippableTheory]
    [InlineData("use_me")]
    [InlineData("")]
    public async Task AC2_legacy_or_empty_capabilities_receive_only_the_rclone_job(string capabilities)
    {
        await ArrangeAsync();
        using var client = await CreateAuthenticatedClientAsync(UniqueEmail("legacy-caps"), Password);

        var seed = await _seedRcloneAndGitJobsAsync(gitJobSeededFirst: false);

        var response = await _acquireAsync(client, capabilities);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var job = await response.Content.ReadFromJsonAsync<Job>();
        job.Should().NotBeNull();
        job!.Id.Should().Be(seed.RcloneJobId);

        await using var context = Fixture.CreateContext();
        var gitJobRow = await context.Jobs.FindAsync(seed.GitJobId);
        gitJobRow!.State.Should().Be(Job.JobState.Ready,
            "the git job must not be parked just because a legacy agent looked at it");
        gitJobRow.Owner.Should().Be("");

        await Fixture.ResetAsync();
    }

    /// <summary>
    /// AC3: an agent advertising both engines can receive both, in either
    /// order (two successive acquire calls, order-agnostic assertion).
    /// </summary>
    [SkippableFact]
    public async Task AC3_agent_advertising_both_engines_can_receive_both()
    {
        await ArrangeAsync();
        using var client = await CreateAuthenticatedClientAsync(UniqueEmail("both-engines"), Password);

        var seed = await _seedRcloneAndGitJobsAsync(gitJobSeededFirst: false);

        var first = await _acquireAsync(client, "rclone,git");
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstJob = await first.Content.ReadFromJsonAsync<Job>();

        var second = await _acquireAsync(client, "rclone,git");
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondJob = await second.Content.ReadFromJsonAsync<Job>();

        new[] { firstJob!.Id, secondJob!.Id }.Should().BeEquivalentTo(
            new[] { seed.RcloneJobId, seed.GitJobId },
            "one acquire must return the rclone job and the other the git job, in either order");

        await Fixture.ResetAsync();
    }

    /// <summary>
    /// AC4: with only a git job Ready, an agent advertising "rclone" must
    /// take the existing not-found path, and the mismatched job must be left
    /// exactly as it was - Ready, unowned - not parked in some other state.
    /// </summary>
    [SkippableFact]
    public async Task AC4_mismatched_agent_gets_not_found_and_the_job_stays_ready_and_unowned()
    {
        await ArrangeAsync();
        using var client = await CreateAuthenticatedClientAsync(UniqueEmail("mismatch"), Password);

        var gitJobId = await _seedSingleGitJobAsync();

        var response = await _acquireAsync(client, "rclone");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using var context = Fixture.CreateContext();
        var jobRow = await context.Jobs.FindAsync(gitJobId);
        jobRow!.State.Should().Be(Job.JobState.Ready);
        jobRow.Owner.Should().Be("");

        await Fixture.ResetAsync();
    }

    private static async Task<HttpResponseMessage> _acquireAsync(HttpClient client, string? capabilities)
    {
        var acquireParams = new AcquireParams
        {
            Username = "capability-test-agent",
            Capabilities = capabilities,
            Owner = $"agent-{Guid.NewGuid():N}",
            Networks = ""
        };
        return await client.PostAsJsonAsync(AcquireRoute, acquireParams);
    }

    private sealed record SeedResult(int RcloneJobId, int GitJobId);

    /// <summary>
    /// Seeds one Ready rclone job and one Ready git job, each with its own
    /// Storage/Endpoint/Rule rows. <paramref name="gitJobSeededFirst"/>
    /// controls both insertion order (so the git job gets the lower id when
    /// true) and which job gets the earlier <c>StartFrom</c>, so AC1 can be
    /// proven independent of both.
    /// </summary>
    private async Task<SeedResult> _seedRcloneAndGitJobsAsync(bool gitJobSeededFirst)
    {
        await using var context = Fixture.CreateContext();

        var now = DateTime.UtcNow;
        var earlier = now.AddMinutes(-10);
        var later = now;

        var rcloneStartFrom = gitJobSeededFirst ? later : earlier;
        var gitStartFrom = gitJobSeededFirst ? earlier : later;

        Job _buildRcloneJob()
        {
            var source = new Storage { UserId = "capability-test", Technology = "local", UriSchema = $"caprc-src-{Guid.NewGuid():N}", IsActive = true };
            var destination = new Storage { UserId = "capability-test", Technology = "smb", UriSchema = $"caprc-dst-{Guid.NewGuid():N}", IsActive = true };
            var sourceEndpoint = new Endpoint { Name = $"caprc-src-ep-{Guid.NewGuid():N}", UserId = "capability-test", Storage = source, Path = "/source", IsActive = true };
            var destinationEndpoint = new Endpoint { Name = $"caprc-dst-ep-{Guid.NewGuid():N}", UserId = "capability-test", Storage = destination, Path = "/destination", IsActive = true };
            var rule = new Rule
            {
                Name = $"caprc-rule-{Guid.NewGuid():N}", Comment = "", UserId = "capability-test",
                SourceEndpoint = sourceEndpoint, DestinationEndpoint = destinationEndpoint,
                Operation = Rule.RuleOperation.Copy
            };
            return new Job
            {
                UserId = "capability-test", Tag = "rclone-job", Operation = Rule.RuleOperation.Copy,
                FromRule = rule, Owner = "", State = Job.JobState.Ready,
                StartFrom = rcloneStartFrom, EndBy = rcloneStartFrom.AddDays(1), LastReported = rcloneStartFrom,
                SourceEndpoint = sourceEndpoint, DestinationEndpoint = destinationEndpoint
            };
        }

        Job _buildGitJob()
        {
            var source = new Storage { UserId = "capability-test", Technology = "git", UriSchema = $"capgit-src-{Guid.NewGuid():N}", Host = "https://github.com/", IsActive = true };
            var destination = new Storage { UserId = "capability-test", Technology = "git", UriSchema = $"capgit-dst-{Guid.NewGuid():N}", Host = "https://codeberg.org/", IsActive = true };
            var sourceEndpoint = new Endpoint { Name = $"capgit-src-ep-{Guid.NewGuid():N}", UserId = "capability-test", Storage = source, Path = "owner/repo-source", IsActive = true };
            var destinationEndpoint = new Endpoint { Name = $"capgit-dst-ep-{Guid.NewGuid():N}", UserId = "capability-test", Storage = destination, Path = "owner/repo-dest", IsActive = true };
            var rule = new Rule
            {
                Name = $"capgit-rule-{Guid.NewGuid():N}", Comment = "", UserId = "capability-test",
                SourceEndpoint = sourceEndpoint, DestinationEndpoint = destinationEndpoint,
                Operation = Rule.RuleOperation.Copy
            };
            return new Job
            {
                UserId = "capability-test", Tag = "git-job", Operation = Rule.RuleOperation.Copy,
                FromRule = rule, Owner = "", State = Job.JobState.Ready,
                StartFrom = gitStartFrom, EndBy = gitStartFrom.AddDays(1), LastReported = gitStartFrom,
                SourceEndpoint = sourceEndpoint, DestinationEndpoint = destinationEndpoint
            };
        }

        Job rcloneJob;
        Job gitJob;
        if (gitJobSeededFirst)
        {
            gitJob = _buildGitJob();
            context.Jobs.Add(gitJob);
            await context.SaveChangesAsync();

            rcloneJob = _buildRcloneJob();
            context.Jobs.Add(rcloneJob);
            await context.SaveChangesAsync();
        }
        else
        {
            rcloneJob = _buildRcloneJob();
            context.Jobs.Add(rcloneJob);
            await context.SaveChangesAsync();

            gitJob = _buildGitJob();
            context.Jobs.Add(gitJob);
            await context.SaveChangesAsync();
        }

        return new SeedResult(rcloneJob.Id, gitJob.Id);
    }

    private async Task<int> _seedSingleGitJobAsync()
    {
        await using var context = Fixture.CreateContext();

        var source = new Storage { UserId = "capability-test", Technology = "git", UriSchema = $"capgit-only-src-{Guid.NewGuid():N}", Host = "https://github.com/", IsActive = true };
        var destination = new Storage { UserId = "capability-test", Technology = "git", UriSchema = $"capgit-only-dst-{Guid.NewGuid():N}", Host = "https://codeberg.org/", IsActive = true };
        var sourceEndpoint = new Endpoint { Name = $"capgit-only-src-ep-{Guid.NewGuid():N}", UserId = "capability-test", Storage = source, Path = "owner/only-source", IsActive = true };
        var destinationEndpoint = new Endpoint { Name = $"capgit-only-dst-ep-{Guid.NewGuid():N}", UserId = "capability-test", Storage = destination, Path = "owner/only-dest", IsActive = true };
        var rule = new Rule
        {
            Name = $"capgit-only-rule-{Guid.NewGuid():N}", Comment = "", UserId = "capability-test",
            SourceEndpoint = sourceEndpoint, DestinationEndpoint = destinationEndpoint,
            Operation = Rule.RuleOperation.Copy
        };
        var now = DateTime.UtcNow;
        var job = new Job
        {
            UserId = "capability-test", Tag = "git-only-job", Operation = Rule.RuleOperation.Copy,
            FromRule = rule, Owner = "", State = Job.JobState.Ready,
            StartFrom = now, EndBy = now.AddDays(1), LastReported = now,
            SourceEndpoint = sourceEndpoint, DestinationEndpoint = destinationEndpoint
        };
        context.Jobs.Add(job);
        await context.SaveChangesAsync();

        return job.Id;
    }
}
