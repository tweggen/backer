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
/// docs/plan-e2e-test-harness.md Gate 6 - Concurrency and safety. Pins the
/// acquisition semantics of <c>HannibalService.AcquireNextJobAsync</c>
/// (application/Hannibal/Services/HannibalServiceJobs.cs) against the real
/// API + PostgreSQL, as the regression net under the capability filter added
/// by docs/plan-git-repo-storage.md Gate C and the coming git engine.
///
/// Rows are seeded directly through the DbContext, exactly like
/// <see cref="JobAcquisitionCapabilityTests"/> - the scheduler is removed in
/// this harness (<see cref="TestSupport.Api.SchedulerMode.Removed"/>), so a
/// Job row only ever exists because a test put it there.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ConcurrencySafetyTests : ApiIntegrationTestBase
{
    private const string Password = "Passw0rd!";
    private const string AcquireRoute = "/api/hannibal/v1/acquireNextJob";
    private const string ReportRoute = "/api/hannibal/v1/reportJob";

    public ConcurrencySafetyTests(PostgresFixture fixture) : base(fixture)
    {
    }

    // ------------------------------------------------------------------
    // AC1 - two agents, one ready job
    // ------------------------------------------------------------------

    /// <summary>
    /// AC1, sequential half: the first of two acquire calls (different Owner
    /// values, i.e. two simulated agents) wins the one Ready job; the second
    /// takes the existing not-found path and must not touch the winner's
    /// ownership.
    /// </summary>
    [SkippableFact]
    public async Task AC1_two_agents_one_ready_job_sequential_first_wins()
    {
        await ArrangeAsync();
        var email = UniqueEmail("two-agents-seq");
        using var client = await CreateAuthenticatedClientAsync(email, Password);
        var userId = await _userIdAsync(email);

        var jobId = await _seedSingleReadyJobAsync("two-agents-seq", ownerUserId: userId);

        var ownerA = $"agent-a-{Guid.NewGuid():N}";
        var ownerB = $"agent-b-{Guid.NewGuid():N}";

        var first = await _acquireAsync(client, ownerA);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstJob = await first.Content.ReadFromJsonAsync<Job>();
        firstJob!.Id.Should().Be(jobId);
        firstJob.Owner.Should().Be(ownerA);

        var second = await _acquireAsync(client, ownerB);
        second.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the job left Ready state the moment the first agent acquired it");

        await using var context = Fixture.CreateContext();
        var jobRow = await context.Jobs.FindAsync(jobId);
        jobRow!.Owner.Should().Be(ownerA,
            "the losing acquire call must never overwrite the winner's Owner");
        jobRow.Owner.Should().NotBe("", "a job must never end up unowned again just because a second agent looked");
        jobRow.State.Should().Be(Job.JobState.Executing);

        await Fixture.ResetAsync();
    }

    /// <summary>
    /// AC1, concurrent half: N simultaneous acquire calls (different Owner
    /// per caller) race one Ready job via <c>Task.WhenAll</c>. Fixed in
    /// <c>AcquireNextJobAsync</c> (HannibalServiceJobs.cs:181-207): the claim
    /// is now a single conditional <c>UPDATE ... WHERE "Id" = @id AND
    /// "State" = 'Ready' AND "Owner" = ''</c> (<c>ExecuteUpdateAsync</c>)
    /// instead of read-then-write, so only the caller whose UPDATE commits
    /// first can match that WHERE clause - every other concurrent UPDATE
    /// necessarily matches zero rows, no matter how the callers' DB round
    /// trips interleave. That makes double-grant structurally impossible
    /// rather than merely unlikely, so this test can assert the invariant
    /// the old comment said could never be asserted: exactly one 200 OK
    /// among N callers. WHICH caller wins is still genuinely nondeterministic
    /// and deliberately not asserted.
    /// </summary>
    [SkippableTheory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task AC1_concurrent_acquires_of_one_job_yield_exactly_one_grant(int callerCount)
    {
        await ArrangeAsync();
        var email = UniqueEmail("two-agents-concurrent");
        using var client = await CreateAuthenticatedClientAsync(email, Password);
        var userId = await _userIdAsync(email);

        var jobId = await _seedSingleReadyJobAsync("two-agents-concurrent", ownerUserId: userId);

        var owners = Enumerable.Range(0, callerCount)
            .Select(i => $"agent-{i}-{Guid.NewGuid():N}")
            .ToArray();

        var responses = await Task.WhenAll(owners.Select(owner => _acquireAsync(client, owner)));

        var winners = responses.Where(r => r.StatusCode == HttpStatusCode.OK).ToList();
        var losers = responses.Where(r => r.StatusCode == HttpStatusCode.NotFound).ToList();

        winners.Should().HaveCount(1, "the guarded UPDATE lets exactly one concurrent caller win the row");
        losers.Should().HaveCount(callerCount - 1);

        var winningJob = await winners[0].Content.ReadFromJsonAsync<Job>();
        winningJob!.Id.Should().Be(jobId);

        await using var context = Fixture.CreateContext();
        var jobRow = await context.Jobs.FindAsync(jobId);
        jobRow!.Owner.Should().Be(winningJob.Owner,
            "the persisted row must carry exactly the winner's owner, never a mix or a reversion to unowned");
        owners.Should().Contain(jobRow.Owner);
        jobRow.State.Should().Be(Job.JobState.Executing);

        await Fixture.ResetAsync();
    }

    // ------------------------------------------------------------------
    // AC2 - PathsOverlap in practice
    // ------------------------------------------------------------------

    /// <summary>
    /// AC2(a): a job writing destination path <c>/a</c> is seeded straight
    /// into <c>Executing</c> (a recent <c>LastReported</c>, so Gate 5's
    /// 120s timeout - out of scope here - never fires). A second Ready job
    /// whose destination sits at <c>/a/b</c> under the SAME storage must not
    /// be handed out while the first is running, and must become acquirable
    /// the moment the first is reported done - proven through the real
    /// report endpoint, not a sleep.
    ///
    /// Both jobs belong to the same user as the acquiring agent on purpose:
    /// <c>_gatherEndpointAccess</c> (HannibalServiceJobs.cs:210-288) scopes
    /// "who already has this endpoint" to the *caller's own* Executing jobs,
    /// so this is the combination the guard is actually built to catch.
    /// </summary>
    [SkippableFact]
    public async Task AC2a_writer_blocks_nested_writer_until_reported_done()
    {
        await ArrangeAsync();
        var email = UniqueEmail("paths-overlap-write");
        using var client = await CreateAuthenticatedClientAsync(email, Password);
        var userId = await _userIdAsync(email);

        int writingJobId;
        int blockedJobId;

        await using (var context = Fixture.CreateContext())
        {
            var destStorage = new Storage
            {
                UserId = userId, Technology = "local", UriSchema = $"po-dest-{Guid.NewGuid():N}", IsActive = true
            };
            context.Storages.Add(destStorage);
            await context.SaveChangesAsync();

            var writingJob = _buildJob(
                userId, "po-writer",
                sourceTech: "local", sourcePath: "/writer-source",
                destStorage: destStorage, destPath: "/a",
                state: Job.JobState.Executing, owner: "existing-writer",
                lastReported: DateTime.UtcNow);
            context.Jobs.Add(writingJob);

            var blockedJob = _buildJob(
                userId, "po-nested",
                sourceTech: "local", sourcePath: "/nested-source",
                destStorage: destStorage, destPath: "/a/b",
                state: Job.JobState.Ready, owner: "",
                lastReported: DateTime.UtcNow);
            context.Jobs.Add(blockedJob);

            await context.SaveChangesAsync();
            writingJobId = writingJob.Id;
            blockedJobId = blockedJob.Id;
        }

        var blockedAttempt = await _acquireAsync(client, "agent-nested");
        blockedAttempt.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the nested destination /a/b overlaps the in-progress write to /a");

        await using (var context = Fixture.CreateContext())
        {
            var stillReady = await context.Jobs.FindAsync(blockedJobId);
            stillReady!.State.Should().Be(Job.JobState.Ready);
            stillReady.Owner.Should().Be("");
        }

        // Report the writer done through the real endpoint - no sleeping.
        var report = await client.PostAsJsonAsync(ReportRoute, new JobStatus
        {
            JobId = writingJobId, Owner = "existing-writer", State = Job.JobState.DoneSuccess
        });
        report.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterReport = await _acquireAsync(client, "agent-nested-retry");
        afterReport.StatusCode.Should().Be(HttpStatusCode.OK,
            "the destination is free the instant the writer is reported done");
        var acquired = await afterReport.Content.ReadFromJsonAsync<Job>();
        acquired!.Id.Should().Be(blockedJobId);

        await Fixture.ResetAsync();
    }

    /// <summary>
    /// AC2(b): two Ready jobs reading the same source endpoint, writing to
    /// two different (non-overlapping) destinations, are both handed out.
    /// Concurrent readers of one source are deliberately allowed
    /// (<c>_mayUseSourceEndpoint</c> only refuses when the conflicting access
    /// is <c>Writing</c>, HannibalServiceJobs.cs:298-318).
    /// </summary>
    [SkippableFact]
    public async Task AC2b_two_readers_of_one_source_are_both_handed_out()
    {
        await ArrangeAsync();
        var email = UniqueEmail("paths-overlap-read");
        using var client = await CreateAuthenticatedClientAsync(email, Password);
        var userId = await _userIdAsync(email);

        int job1Id;
        int job2Id;

        await using (var context = Fixture.CreateContext())
        {
            var sharedSource = new Storage
            {
                UserId = userId, Technology = "local", UriSchema = $"po-src-{Guid.NewGuid():N}", IsActive = true
            };
            context.Storages.Add(sharedSource);
            await context.SaveChangesAsync();

            var sharedSourceEndpoint = new Endpoint
            {
                Name = $"po-src-ep-{Guid.NewGuid():N}", UserId = userId, Storage = sharedSource,
                Path = "/shared-source", IsActive = true
            };
            context.Endpoints.Add(sharedSourceEndpoint);
            await context.SaveChangesAsync();

            var dest1 = _buildStorageAndEndpoint(userId, "po-dest1", "/dest1", out var dest1Endpoint);
            var dest2 = _buildStorageAndEndpoint(userId, "po-dest2", "/dest2", out var dest2Endpoint);
            context.Storages.AddRange(dest1, dest2);
            context.Endpoints.AddRange(dest1Endpoint, dest2Endpoint);
            await context.SaveChangesAsync();

            var now = DateTime.UtcNow;
            var job1 = new Job
            {
                UserId = userId, Tag = "po-reader-1", Operation = Rule.RuleOperation.Copy,
                FromRule = _buildRule(userId, "po-reader-1", sharedSourceEndpoint, dest1Endpoint),
                Owner = "", State = Job.JobState.Ready,
                StartFrom = now.AddMinutes(-5), EndBy = now.AddDays(1), LastReported = now,
                SourceEndpoint = sharedSourceEndpoint, DestinationEndpoint = dest1Endpoint
            };
            var job2 = new Job
            {
                UserId = userId, Tag = "po-reader-2", Operation = Rule.RuleOperation.Copy,
                FromRule = _buildRule(userId, "po-reader-2", sharedSourceEndpoint, dest2Endpoint),
                Owner = "", State = Job.JobState.Ready,
                StartFrom = now.AddMinutes(-4), EndBy = now.AddDays(1), LastReported = now,
                SourceEndpoint = sharedSourceEndpoint, DestinationEndpoint = dest2Endpoint
            };
            context.Jobs.AddRange(job1, job2);
            await context.SaveChangesAsync();
            job1Id = job1.Id;
            job2Id = job2.Id;
        }

        var first = await _acquireAsync(client, "reader-agent-1");
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstJob = await first.Content.ReadFromJsonAsync<Job>();

        var second = await _acquireAsync(client, "reader-agent-2");
        second.StatusCode.Should().Be(HttpStatusCode.OK,
            "a second reader of the same source must not be blocked by the first");
        var secondJob = await second.Content.ReadFromJsonAsync<Job>();

        new[] { firstJob!.Id, secondJob!.Id }.Should().BeEquivalentTo(new[] { job1Id, job2Id });

        await Fixture.ResetAsync();
    }

    // ------------------------------------------------------------------
    // AC3 - Networks filtering
    // ------------------------------------------------------------------

    /// <summary>
    /// AC3: a storage with <c>Networks = "netA"</c> on the source endpoint.
    /// An acquire call from <c>"netB"</c> never receives the job (not-found,
    /// job stays Ready/unowned); the same job acquired from <c>"netA"</c>
    /// succeeds. HostIsolationTests.cs has no Networks coverage at all (it
    /// is about the test host's own isolation from the developer's database
    /// and secrets, a different meaning of "isolation") - this is the first
    /// explicit pair for HannibalServiceJobs.cs:153-159.
    /// </summary>
    [SkippableFact]
    public async Task AC3_networks_filter_blocks_the_wrong_network_and_allows_the_right_one()
    {
        await ArrangeAsync();
        var email = UniqueEmail("networks");
        using var client = await CreateAuthenticatedClientAsync(email, Password);
        var userId = await _userIdAsync(email);

        int jobId;
        await using (var context = Fixture.CreateContext())
        {
            var source = new Storage
            {
                UserId = userId, Technology = "local", UriSchema = $"net-src-{Guid.NewGuid():N}",
                Networks = "netA", IsActive = true
            };
            var destination = new Storage
            {
                UserId = userId, Technology = "smb", UriSchema = $"net-dst-{Guid.NewGuid():N}",
                IsActive = true
            };
            context.Storages.AddRange(source, destination);

            var sourceEndpoint = new Endpoint
            {
                Name = $"net-src-ep-{Guid.NewGuid():N}", UserId = userId, Storage = source,
                Path = "/source", IsActive = true
            };
            var destinationEndpoint = new Endpoint
            {
                Name = $"net-dst-ep-{Guid.NewGuid():N}", UserId = userId, Storage = destination,
                Path = "/destination", IsActive = true
            };
            context.Endpoints.AddRange(sourceEndpoint, destinationEndpoint);

            var now = DateTime.UtcNow;
            var job = new Job
            {
                UserId = userId, Tag = "networks-job", Operation = Rule.RuleOperation.Copy,
                FromRule = _buildRule(userId, "networks-job", sourceEndpoint, destinationEndpoint),
                Owner = "", State = Job.JobState.Ready,
                StartFrom = now, EndBy = now.AddDays(1), LastReported = now,
                SourceEndpoint = sourceEndpoint, DestinationEndpoint = destinationEndpoint
            };
            context.Jobs.Add(job);
            await context.SaveChangesAsync();
            jobId = job.Id;
        }

        var wrongNetwork = await _acquireAsync(client, "agent-wrong-net", networks: "netB");
        wrongNetwork.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using (var context = Fixture.CreateContext())
        {
            var stillReady = await context.Jobs.FindAsync(jobId);
            stillReady!.State.Should().Be(Job.JobState.Ready);
            stillReady.Owner.Should().Be("");
        }

        var rightNetwork = await _acquireAsync(client, "agent-right-net", networks: "netA");
        rightNetwork.StatusCode.Should().Be(HttpStatusCode.OK);
        var acquired = await rightNetwork.Content.ReadFromJsonAsync<Job>();
        acquired!.Id.Should().Be(jobId);

        await Fixture.ResetAsync();
    }

    // ------------------------------------------------------------------
    // AC4 - user isolation at acquisition
    // ------------------------------------------------------------------

    /// <summary>
    /// AC4: user B's agent must never receive user A's job.
    /// <c>AcquireNextJobAsync</c>'s candidate query (HannibalServiceJobs.cs
    /// :128-133) now includes <c>j.UserId == _currentUser.Id</c>, so user A's
    /// Ready job is simply not in user B's candidate set - B's acquire takes
    /// the existing not-found path, and the job is left exactly as seeded
    /// until A's own agent acquires it.
    /// </summary>
    [SkippableFact]
    public async Task AC4_user_isolation_is_enforced_at_acquisition()
    {
        await ArrangeAsync();
        var emailA = UniqueEmail("user-iso-a");
        var emailB = UniqueEmail("user-iso-b");
        using var clientA = await CreateAuthenticatedClientAsync(emailA, Password);
        using var clientB = await CreateAuthenticatedClientAsync(emailB, Password);
        var userAId = await _userIdAsync(emailA);

        var jobId = await _seedSingleReadyJobAsync("user-iso", ownerUserId: userAId);

        var acquiredByB = await _acquireAsync(clientB, "agent-b");
        acquiredByB.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "user B's agent must not see a job belonging to user A");

        await using (var context = Fixture.CreateContext())
        {
            var stillReady = await context.Jobs.FindAsync(jobId);
            stillReady!.State.Should().Be(Job.JobState.Ready);
            stillReady.Owner.Should().Be("");
        }

        var acquiredByA = await _acquireAsync(clientA, "agent-a");
        acquiredByA.StatusCode.Should().Be(HttpStatusCode.OK);
        var jobSeenByA = await acquiredByA.Content.ReadFromJsonAsync<Job>();
        jobSeenByA!.Id.Should().Be(jobId, "user A's own agent must still be able to acquire user A's job");

        await Fixture.ResetAsync();
    }

    // ------------------------------------------------------------------
    // AC5 - capability filtering composes with networks and user
    // ------------------------------------------------------------------

    /// <summary>
    /// AC5: one Ready rclone job and one Ready git job for the same user,
    /// both endpoints' storages on the same network. An agent advertising
    /// only "git", on that network, authenticated as that user, receives
    /// exactly the git job - proving the capability filter
    /// (HannibalServiceJobs.cs:142-151) composes correctly with the network
    /// filter (:153-167) rather than one silently subsuming the other: since
    /// both jobs sit on the same network, only the capability check can be
    /// the reason the rclone job is excluded. Gate C's own tests
    /// (<see cref="JobAcquisitionCapabilityTests"/>) cover capability
    /// filtering alone; this is the composition case the gate asks for.
    /// </summary>
    [SkippableFact]
    public async Task AC5_capability_filter_composes_with_networks_and_user()
    {
        await ArrangeAsync();
        var email = UniqueEmail("compose");
        using var client = await CreateAuthenticatedClientAsync(email, Password);
        var userId = await _userIdAsync(email);

        const string network = "netCompose";
        int rcloneJobId;
        int gitJobId;

        await using (var context = Fixture.CreateContext())
        {
            var now = DateTime.UtcNow;

            var rcloneSource = new Storage
            {
                UserId = userId, Technology = "local", UriSchema = $"compose-rc-src-{Guid.NewGuid():N}",
                Networks = network, IsActive = true
            };
            var rcloneDest = new Storage
            {
                UserId = userId, Technology = "smb", UriSchema = $"compose-rc-dst-{Guid.NewGuid():N}",
                Networks = network, IsActive = true
            };
            var rcloneSourceEp = new Endpoint
            {
                Name = $"compose-rc-src-ep-{Guid.NewGuid():N}", UserId = userId, Storage = rcloneSource,
                Path = "/source", IsActive = true
            };
            var rcloneDestEp = new Endpoint
            {
                Name = $"compose-rc-dst-ep-{Guid.NewGuid():N}", UserId = userId, Storage = rcloneDest,
                Path = "/destination", IsActive = true
            };
            context.Storages.AddRange(rcloneSource, rcloneDest);
            context.Endpoints.AddRange(rcloneSourceEp, rcloneDestEp);

            var gitSource = new Storage
            {
                UserId = userId, Technology = "git", UriSchema = $"compose-git-src-{Guid.NewGuid():N}",
                Host = "https://github.com/", Networks = network, IsActive = true
            };
            var gitDest = new Storage
            {
                UserId = userId, Technology = "git", UriSchema = $"compose-git-dst-{Guid.NewGuid():N}",
                Host = "https://codeberg.org/", Networks = network, IsActive = true
            };
            var gitSourceEp = new Endpoint
            {
                Name = $"compose-git-src-ep-{Guid.NewGuid():N}", UserId = userId, Storage = gitSource,
                Path = "owner/repo-source", IsActive = true
            };
            var gitDestEp = new Endpoint
            {
                Name = $"compose-git-dst-ep-{Guid.NewGuid():N}", UserId = userId, Storage = gitDest,
                Path = "owner/repo-dest", IsActive = true
            };
            context.Storages.AddRange(gitSource, gitDest);
            context.Endpoints.AddRange(gitSourceEp, gitDestEp);

            var rcloneJob = new Job
            {
                UserId = userId, Tag = "compose-rclone", Operation = Rule.RuleOperation.Copy,
                FromRule = _buildRule(userId, "compose-rclone", rcloneSourceEp, rcloneDestEp),
                Owner = "", State = Job.JobState.Ready,
                StartFrom = now, EndBy = now.AddDays(1), LastReported = now,
                SourceEndpoint = rcloneSourceEp, DestinationEndpoint = rcloneDestEp
            };
            var gitJob = new Job
            {
                UserId = userId, Tag = "compose-git", Operation = Rule.RuleOperation.Copy,
                FromRule = _buildRule(userId, "compose-git", gitSourceEp, gitDestEp),
                Owner = "", State = Job.JobState.Ready,
                StartFrom = now, EndBy = now.AddDays(1), LastReported = now,
                SourceEndpoint = gitSourceEp, DestinationEndpoint = gitDestEp
            };
            context.Jobs.AddRange(rcloneJob, gitJob);
            await context.SaveChangesAsync();
            rcloneJobId = rcloneJob.Id;
            gitJobId = gitJob.Id;
        }

        var gitAgent = await _acquireAsync(client, "git-agent", capabilities: "git", networks: network);
        gitAgent.StatusCode.Should().Be(HttpStatusCode.OK);
        var acquiredByGitAgent = await gitAgent.Content.ReadFromJsonAsync<Job>();
        acquiredByGitAgent!.Id.Should().Be(gitJobId, "capability filtering must pick the git job, not the rclone one, " +
                                                      "even though both sit on the same network for the same user");

        await using (var context = Fixture.CreateContext())
        {
            var rcloneRow = await context.Jobs.FindAsync(rcloneJobId);
            rcloneRow!.State.Should().Be(Job.JobState.Ready,
                "the rclone job must be left untouched by an agent that cannot run it");
            rcloneRow.Owner.Should().Be("");
        }

        // And the normal path still works: an rclone agent on the same
        // network/user gets the remaining rclone job.
        var rcloneAgent = await _acquireAsync(client, "rclone-agent", capabilities: "rclone", networks: network);
        rcloneAgent.StatusCode.Should().Be(HttpStatusCode.OK);
        var acquiredByRcloneAgent = await rcloneAgent.Content.ReadFromJsonAsync<Job>();
        acquiredByRcloneAgent!.Id.Should().Be(rcloneJobId);

        await Fixture.ResetAsync();
    }

    // ------------------------------------------------------------------
    // Shared helpers
    // ------------------------------------------------------------------

    private static async Task<HttpResponseMessage> _acquireAsync(
        HttpClient client, string owner, string capabilities = "rclone", string networks = "")
    {
        var acquireParams = new AcquireParams
        {
            Username = "concurrency-test-agent",
            Capabilities = capabilities,
            Owner = owner,
            Networks = networks
        };
        return await client.PostAsJsonAsync(AcquireRoute, acquireParams);
    }

    /// <summary>
    /// Resolves the Identity user id (a GUID string) behind an e-mail that
    /// was just registered through <see cref="ApiIntegrationTestBase.CreateAuthenticatedClientAsync"/>,
    /// the same way <see cref="TokenEndpointTests"/> does.
    /// </summary>
    private async Task<string> _userIdAsync(string email)
    {
        using var scope = Api.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByEmailAsync(email);
        user.Should().NotBeNull();
        return user!.Id;
    }

    /// <summary>
    /// Seeds one Ready rclone job with its own Storage/Endpoint/Rule rows and
    /// returns its id. <paramref name="ownerUserId"/> defaults to a fixed
    /// per-test-prefix string when the test does not care which user owns
    /// the row (acquisition today applies no UserId filter at all - see AC4).
    /// </summary>
    private async Task<int> _seedSingleReadyJobAsync(string prefix, string? ownerUserId = null)
    {
        var userId = ownerUserId ?? $"{prefix}-user";

        await using var context = Fixture.CreateContext();

        var source = new Storage
        {
            UserId = userId, Technology = "local", UriSchema = $"{prefix}-src-{Guid.NewGuid():N}", IsActive = true
        };
        var destination = new Storage
        {
            UserId = userId, Technology = "smb", UriSchema = $"{prefix}-dst-{Guid.NewGuid():N}", IsActive = true
        };
        context.Storages.AddRange(source, destination);

        var sourceEndpoint = new Endpoint
        {
            Name = $"{prefix}-src-ep-{Guid.NewGuid():N}", UserId = userId, Storage = source,
            Path = "/source", IsActive = true
        };
        var destinationEndpoint = new Endpoint
        {
            Name = $"{prefix}-dst-ep-{Guid.NewGuid():N}", UserId = userId, Storage = destination,
            Path = "/destination", IsActive = true
        };
        context.Endpoints.AddRange(sourceEndpoint, destinationEndpoint);

        var now = DateTime.UtcNow;
        var job = new Job
        {
            UserId = userId, Tag = $"{prefix}-job", Operation = Rule.RuleOperation.Copy,
            FromRule = _buildRule(userId, prefix, sourceEndpoint, destinationEndpoint),
            Owner = "", State = Job.JobState.Ready,
            StartFrom = now, EndBy = now.AddDays(1), LastReported = now,
            SourceEndpoint = sourceEndpoint, DestinationEndpoint = destinationEndpoint
        };
        context.Jobs.Add(job);
        await context.SaveChangesAsync();

        return job.Id;
    }

    private static Rule _buildRule(string userId, string tagPrefix, Endpoint source, Endpoint destination) => new()
    {
        Name = $"{tagPrefix}-rule-{Guid.NewGuid():N}", Comment = "", UserId = userId,
        SourceEndpoint = source, DestinationEndpoint = destination,
        Operation = Rule.RuleOperation.Copy
    };

    /// <summary>
    /// Builds (not yet saved) a Job with its own source Storage/Endpoint, an
    /// already-existing destination Storage/Endpoint pair, and a backing
    /// Rule. Used where the destination Storage must be *shared* across two
    /// jobs (AC2a).
    /// </summary>
    private Job _buildJob(
        string userId, string tagPrefix,
        string sourceTech, string sourcePath,
        Storage destStorage, string destPath,
        Job.JobState state, string owner, DateTime lastReported)
    {
        var sourceStorage = new Storage
        {
            UserId = userId, Technology = sourceTech, UriSchema = $"{tagPrefix}-src-{Guid.NewGuid():N}",
            IsActive = true
        };
        var sourceEndpoint = new Endpoint
        {
            Name = $"{tagPrefix}-src-ep-{Guid.NewGuid():N}", UserId = userId, Storage = sourceStorage,
            Path = sourcePath, IsActive = true
        };
        var destinationEndpoint = new Endpoint
        {
            Name = $"{tagPrefix}-dst-ep-{Guid.NewGuid():N}", UserId = userId, Storage = destStorage,
            Path = destPath, IsActive = true
        };

        var now = DateTime.UtcNow;
        var job = new Job
        {
            UserId = userId, Tag = tagPrefix, Operation = Rule.RuleOperation.Copy,
            FromRule = _buildRule(userId, tagPrefix, sourceEndpoint, destinationEndpoint),
            Owner = owner, State = state,
            StartFrom = now.AddMinutes(-1), EndBy = now.AddDays(1), LastReported = lastReported,
            SourceEndpoint = sourceEndpoint, DestinationEndpoint = destinationEndpoint
        };

        return job;
    }

    private static Storage _buildStorageAndEndpoint(
        string userId, string tagPrefix, string path, out Endpoint endpoint)
    {
        var storage = new Storage
        {
            UserId = userId, Technology = "local", UriSchema = $"{tagPrefix}-{Guid.NewGuid():N}", IsActive = true
        };
        endpoint = new Endpoint
        {
            Name = $"{tagPrefix}-ep-{Guid.NewGuid():N}", UserId = userId, Storage = storage,
            Path = path, IsActive = true
        };
        return storage;
    }
}
