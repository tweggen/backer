using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Hannibal.Data;
using Hannibal.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hannibal.IntegrationTests;

/// <summary>
/// docs/plan-e2e-test-harness.md Gate 5 AC5 - the server times out silent
/// jobs. Pins the actual behaviour of <c>_gatherEndpointAccess</c>
/// (application/Hannibal/Services/HannibalServiceJobs.cs:236-314), which runs
/// on every <c>AcquireNextJobAsync</c> call and, for the *calling user's own*
/// Executing jobs, treats any job whose <c>LastReported</c> is more than 120
/// seconds old (HannibalServiceJobs.cs:243,252-253) as timed out.
///
/// Contrary to the plan text's "becomes re-acquirable", the code does NOT
/// return the timed-out job to Ready - it is moved straight to the terminal
/// state <c>Job.JobState.DoneFailure</c> (HannibalServiceJobs.cs:308,
/// inside the "if (listTimedOut.Count > 0)" block at :304-311) and is never
/// considered as an acquisition candidate again. What actually becomes
/// re-acquirable is not the stale job itself but the *endpoints* it was
/// holding: because a timed-out job is skipped by the "add to mapStates"
/// branch entirely (the age check at :253 sends it to listTimedOut instead of
/// into the Reading/Writing bookkeeping at :258-301), a Ready job on the same
/// endpoints is no longer blocked by it on the very next acquire call.
///
/// This is the contract the upcoming git engine's heartbeat
/// (docs/plan-git-repo-storage.md §6 "Heartbeat") exists to satisfy: keep
/// LastReported fresh, or the server declares the job dead and frees its
/// endpoints out from under you.
///
/// No fake clock exists server-side and none is needed here - the timeout
/// is a pure function of the seeded LastReported value versus DateTime.UtcNow
/// at the moment of the acquire call, so the stale/fresh cases are produced
/// by seeding LastReported directly, exactly like
/// <see cref="ConcurrencySafetyTests"/> AC2a's "recent LastReported ... never
/// fires" comment already relies on for the negative direction.
/// </summary>
[Collection(PostgresCollection.Name)]
public class JobTimeoutContractTests : ApiIntegrationTestBase
{
    private const string Password = "Passw0rd!";
    private const string AcquireRoute = "/api/hannibal/v1/acquireNextJob";

    public JobTimeoutContractTests(PostgresFixture fixture) : base(fixture)
    {
    }

    /// <summary>
    /// Test 1 - the timeout itself. A stale Executing job (LastReported 3
    /// minutes ago, well past the 120s threshold) sits on the same endpoints
    /// as a second, Ready job for the same user. An acquire call:
    ///  (a) hands out the Ready job - the stale job no longer blocks its
    ///      endpoints, because _gatherEndpointAccess routes it into
    ///      listTimedOut instead of into the Reading/Writing map, and
    ///  (b) leaves the stale job's row in the terminal state the code
    ///      actually assigns, Job.JobState.DoneFailure, not Executing and not
    ///      back to Ready.
    /// </summary>
    [SkippableFact]
    public async Task Stale_executing_job_is_marked_DoneFailure_and_stops_blocking_its_endpoints()
    {
        await ArrangeAsync();
        var email = UniqueEmail("timeout-stale");
        using var client = await CreateAuthenticatedClientAsync(email, Password);
        var userId = await _userIdAsync(email);

        int staleJobId;
        int readyJobId;

        await using (var context = Fixture.CreateContext())
        {
            var source = new Storage
            {
                UserId = userId, Technology = "local", UriSchema = $"timeout-src-{Guid.NewGuid():N}", IsActive = true
            };
            var destination = new Storage
            {
                UserId = userId, Technology = "smb", UriSchema = $"timeout-dst-{Guid.NewGuid():N}", IsActive = true
            };
            context.Storages.AddRange(source, destination);

            var sourceEndpoint = new Endpoint
            {
                Name = $"timeout-src-ep-{Guid.NewGuid():N}", UserId = userId, Storage = source,
                Path = "/source", IsActive = true
            };
            var destinationEndpoint = new Endpoint
            {
                Name = $"timeout-dst-ep-{Guid.NewGuid():N}", UserId = userId, Storage = destination,
                Path = "/destination", IsActive = true
            };
            context.Endpoints.AddRange(sourceEndpoint, destinationEndpoint);

            var now = DateTime.UtcNow;

            // The stale job: Executing, owned, but silent for 3 minutes -
            // past the 120s threshold at HannibalServiceJobs.cs:243.
            var staleJob = new Job
            {
                UserId = userId, Tag = "timeout-stale", Operation = Rule.RuleOperation.Copy,
                FromRule = _buildRule(userId, "timeout-stale", sourceEndpoint, destinationEndpoint),
                Owner = "dead-agent", State = Job.JobState.Executing,
                StartFrom = now.AddMinutes(-5), EndBy = now.AddDays(1),
                LastReported = now - TimeSpan.FromMinutes(3),
                SourceEndpoint = sourceEndpoint, DestinationEndpoint = destinationEndpoint
            };

            // A second Ready job on the SAME endpoints - would be blocked by
            // the stale job's writer/reader claim if that job were still
            // considered alive.
            var readyJob = new Job
            {
                UserId = userId, Tag = "timeout-ready", Operation = Rule.RuleOperation.Copy,
                FromRule = _buildRule(userId, "timeout-ready", sourceEndpoint, destinationEndpoint),
                Owner = "", State = Job.JobState.Ready,
                StartFrom = now.AddMinutes(-1), EndBy = now.AddDays(1), LastReported = now,
                SourceEndpoint = sourceEndpoint, DestinationEndpoint = destinationEndpoint
            };

            context.Jobs.AddRange(staleJob, readyJob);
            await context.SaveChangesAsync();
            staleJobId = staleJob.Id;
            readyJobId = readyJob.Id;
        }

        var acquire = await _acquireAsync(client, "agent-after-timeout");
        acquire.StatusCode.Should().Be(HttpStatusCode.OK,
            "the stale job's endpoints must no longer block the Ready job once it has timed out");
        var acquired = await acquire.Content.ReadFromJsonAsync<Job>();
        acquired!.Id.Should().Be(readyJobId);

        await using (var context = Fixture.CreateContext())
        {
            var staleRow = await context.Jobs.FindAsync(staleJobId);
            staleRow!.State.Should().Be(Job.JobState.DoneFailure,
                "HannibalServiceJobs.cs:304-311 moves a timed-out job to DoneFailure, " +
                "not back to Ready - it does not become re-acquirable itself");
            staleRow.State.Should().NotBe(Job.JobState.Executing);
        }

        await Fixture.ResetAsync();
    }

    /// <summary>
    /// Test 2 - the boundary negative. An Executing job with a fresh
    /// LastReported (now) is NOT timed out by an acquire call: it stays
    /// Executing, stays owned by the same owner, and - because it is still
    /// alive as far as _gatherEndpointAccess is concerned - it still blocks a
    /// Ready job on the same endpoints (acquire returns not-found). This is
    /// the half of the contract the heartbeat exists to satisfy: reporting
    /// keeps you alive.
    /// </summary>
    [SkippableFact]
    public async Task Fresh_executing_job_is_not_timed_out_and_still_blocks_its_endpoints()
    {
        await ArrangeAsync();
        var email = UniqueEmail("timeout-fresh");
        using var client = await CreateAuthenticatedClientAsync(email, Password);
        var userId = await _userIdAsync(email);

        int freshJobId;
        int blockedJobId;

        await using (var context = Fixture.CreateContext())
        {
            var source = new Storage
            {
                UserId = userId, Technology = "local", UriSchema = $"fresh-src-{Guid.NewGuid():N}", IsActive = true
            };
            var destination = new Storage
            {
                UserId = userId, Technology = "smb", UriSchema = $"fresh-dst-{Guid.NewGuid():N}", IsActive = true
            };
            context.Storages.AddRange(source, destination);

            var sourceEndpoint = new Endpoint
            {
                Name = $"fresh-src-ep-{Guid.NewGuid():N}", UserId = userId, Storage = source,
                Path = "/source", IsActive = true
            };
            var destinationEndpoint = new Endpoint
            {
                Name = $"fresh-dst-ep-{Guid.NewGuid():N}", UserId = userId, Storage = destination,
                Path = "/destination", IsActive = true
            };
            context.Endpoints.AddRange(sourceEndpoint, destinationEndpoint);

            var now = DateTime.UtcNow;

            // Fresh: Executing, LastReported = now - well under the 120s
            // threshold, must survive the age check at HannibalServiceJobs.cs:252-253.
            var freshJob = new Job
            {
                UserId = userId, Tag = "timeout-fresh", Operation = Rule.RuleOperation.Copy,
                FromRule = _buildRule(userId, "timeout-fresh", sourceEndpoint, destinationEndpoint),
                Owner = "live-agent", State = Job.JobState.Executing,
                StartFrom = now.AddMinutes(-1), EndBy = now.AddDays(1), LastReported = now,
                SourceEndpoint = sourceEndpoint, DestinationEndpoint = destinationEndpoint
            };

            // A second Ready job on the SAME endpoints, which the fresh job
            // should keep blocked.
            var blockedJob = new Job
            {
                UserId = userId, Tag = "timeout-blocked", Operation = Rule.RuleOperation.Copy,
                FromRule = _buildRule(userId, "timeout-blocked", sourceEndpoint, destinationEndpoint),
                Owner = "", State = Job.JobState.Ready,
                StartFrom = now.AddMinutes(-1), EndBy = now.AddDays(1), LastReported = now,
                SourceEndpoint = sourceEndpoint, DestinationEndpoint = destinationEndpoint
            };

            context.Jobs.AddRange(freshJob, blockedJob);
            await context.SaveChangesAsync();
            freshJobId = freshJob.Id;
            blockedJobId = blockedJob.Id;
        }

        var acquire = await _acquireAsync(client, "agent-blocked-by-fresh");
        acquire.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the fresh job is still alive as far as the server knows, so it must keep blocking its endpoints");

        await using (var context = Fixture.CreateContext())
        {
            var freshRow = await context.Jobs.FindAsync(freshJobId);
            freshRow!.State.Should().Be(Job.JobState.Executing,
                "a job reporting within the 120s window must never be timed out");
            freshRow.Owner.Should().Be("live-agent");

            var blockedRow = await context.Jobs.FindAsync(blockedJobId);
            blockedRow!.State.Should().Be(Job.JobState.Ready);
            blockedRow.Owner.Should().Be("");
        }

        await Fixture.ResetAsync();
    }

    // ------------------------------------------------------------------
    // Shared helpers (mirrors ConcurrencySafetyTests' private helpers)
    // ------------------------------------------------------------------

    private static async Task<HttpResponseMessage> _acquireAsync(
        HttpClient client, string owner, string capabilities = "rclone", string networks = "")
    {
        var acquireParams = new AcquireParams
        {
            Username = "timeout-test-agent",
            Capabilities = capabilities,
            Owner = owner,
            Networks = networks
        };
        return await client.PostAsJsonAsync(AcquireRoute, acquireParams);
    }

    /// <summary>
    /// Resolves the Identity user id (a GUID string) behind an e-mail that
    /// was just registered through <see cref="ApiIntegrationTestBase.CreateAuthenticatedClientAsync"/>,
    /// exactly like <see cref="ConcurrencySafetyTests"/>'s helper of the same
    /// name - acquisition (and _gatherEndpointAccess) filters by the real
    /// Identity user id, so a placeholder UserId would never be seen.
    /// </summary>
    private async Task<string> _userIdAsync(string email)
    {
        using var scope = Api.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByEmailAsync(email);
        user.Should().NotBeNull();
        return user!.Id;
    }

    private static Rule _buildRule(string userId, string tagPrefix, Endpoint source, Endpoint destination) => new()
    {
        Name = $"{tagPrefix}-rule-{Guid.NewGuid():N}", Comment = "", UserId = userId,
        SourceEndpoint = source, DestinationEndpoint = destination,
        Operation = Rule.RuleOperation.Copy
    };
}
