using System.Net.Http.Json;
using Backer.E2ETests.TestSupport;
using FluentAssertions;
using Hannibal.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TestSupport.Api;
using Xunit;

namespace Backer.E2ETests;

/// <summary>
/// Gate D's reason to exist (docs/plan-git-repo-storage.md): a git+git Copy
/// rule, now allowed by the Gate D flip in
/// <c>HannibalServiceRules._validateRuleEndpoints</c>, created entirely over
/// REST, runs end to end through the real scheduler, the real agent and the
/// real <c>worker/WorkerGit</c> mirror engine (real "git" on PATH, no rclone
/// involved) - the same full-loop shape as <see cref="FullLoopTests"/>, but
/// for the git engine instead of rclone.
/// </summary>
[Collection(PostgresCollection.Name)]
public class GitMirrorHappyPathTests
{
    private readonly PostgresFixture _fixture;

    public GitMirrorHappyPathTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task GitPlusGitCopyRule_MirrorsTheSourceBranch_AndTheJobReachesDoneSuccess()
    {
        _fixture.SkipIfUnavailable();
        await _fixture.ResetAsync();

        await using var harness = await FullLoopHarness.StartAsync(_fixture);

        var sourceHost = Directory.CreateTempSubdirectory("backer-git-e2e-src-").FullName;
        var destinationHost = Directory.CreateTempSubdirectory("backer-git-e2e-dst-").FullName;

        var sourceBare = Path.Combine(sourceHost, "repo.git");
        GitFixtureRepo.InitBare(sourceBare);
        var work = Path.Combine(sourceHost, "work");
        GitFixtureRepo.CloneToWorkdir(sourceBare, work);
        GitFixtureRepo.CreateOrphanBranch(work, "main");
        var headSha = GitFixtureRepo.Commit(work, "a.txt", "hello from the e2e happy path", "init");
        GitFixtureRepo.Push(work, "refs/heads/*:refs/heads/*");

        var destinationBare = Path.Combine(destinationHost, "repo.git");
        // Plan §1: "the destination repository must already exist (it may be
        // empty)" - Gate D does not auto-create it.
        GitFixtureRepo.InitBare(destinationBare);

        var sourceStorageId = await _createGitStorageAsync(harness, "e2egitsrc", sourceHost);
        var destinationStorageId = await _createGitStorageAsync(harness, "e2egitdst", destinationHost);

        var sourceEndpointId = await _createEndpointAsync(harness, sourceStorageId, "repo.git");
        var destinationEndpointId = await _createEndpointAsync(harness, destinationStorageId, "repo.git");

        var ruleResponse = await harness.Client.PostAsJsonAsync(
            "/api/hannibal/v1/rules",
            new Rule
            {
                Name = "e2e git copy rule",
                SourceEndpointId = sourceEndpointId,
                DestinationEndpointId = destinationEndpointId,
                Operation = Rule.RuleOperation.Copy,
                MaxDestinationAge = TimeSpan.FromHours(1),
                MinRetryTime = TimeSpan.FromMinutes(15),
                MaxTimeAfterSourceModification = TimeSpan.FromMinutes(30),
                DailyTriggerTime = TimeSpan.FromHours(3)
            });

        ruleResponse.EnsureSuccessStatusCode();
        var rule = await ruleResponse.Content.ReadFromJsonAsync<CreateRuleResult>();

        var job = await harness.WaitForAsync(
            async context => await context.Jobs
                .Where(j => j.FromRuleId == rule!.Id && j.State == Job.JobState.DoneSuccess)
                .FirstOrDefaultAsync(),
            "the git job to be reported as DoneSuccess",
            timeout: TimeSpan.FromSeconds(60));

        job.Operation.Should().Be(Rule.RuleOperation.Copy);
        job.Owner.Should().BeEmpty("ReportJobAsync releases the job when it completes");

        var destinationRefs = GitFixtureRepo.ForEachRef(destinationBare);
        destinationRefs.Should().ContainKey("refs/heads/main");
        destinationRefs["refs/heads/main"].Should().Be(headSha,
            "the destination must carry the source's branch at exactly the source's SHA");
    }

    /// <summary>
    /// docs/plan-git-repo-storage.md Gate F AC1 (the full-loop half): once the
    /// first mirror has completed <c>DoneSuccess</c>, a SECOND job for the
    /// same rule - the source unchanged in between - must also complete
    /// <c>DoneSuccess</c>, but through the §4 "already in sync" fast path:
    /// no fetch, no push, and the destination's refs untouched.
    ///
    /// <para>No REST trigger endpoint exists for "run this rule again"
    /// (<c>Api/Program.cs</c> has no such route - the only rule-related
    /// broadcast is <c>HannibalServiceRules.cs:176</c>'s <c>NewJobAvailable</c>
    /// on rule creation, not on demand), so the second job is seeded directly
    /// against the same database the API and agent both use, exactly like
    /// <see cref="Hannibal.IntegrationTests.SchedulerDeterminismTests"/> seeds
    /// jobs. The scheduler itself is not asked to mint it - at real
    /// <c>MaxDestinationAge = 1h</c> wall-clock time it would not do so for
    /// nearly an hour - so this is deliberately the same "GitWorkerService
    /// notices a Ready job" path a genuinely re-triggered rule would use.</para>
    ///
    /// <para>The agent is nudged the same way production nudges it -
    /// broadcasting <c>NewJobAvailable</c> on the real hub
    /// (<c>HannibalServiceJobs.cs:486</c>'s production trigger) - rather than
    /// waiting on <c>GitWorkerService</c>'s 120s safety-net poll
    /// (<c>GitWorkerService.cs:51</c>), which would make this test needlessly
    /// slow.</para>
    ///
    /// <para><b>No in-process fast-path log/flag assertion</b>: a log-capturing
    /// <c>ILoggerProvider</c> was tried and confirmed non-viable -
    /// <c>tests/TestSupport.Agent/AgentHostFactory.cs:264-269</c> documents that
    /// <c>BackerAgent/Program.cs</c>'s <c>builder.Host.UseSerilog()</c>
    /// (parameterless) replaces the logger factory outright, so no provider a
    /// test adds ever receives anything - a pre-existing, intentional harness
    /// limitation, not something this gate should work around. Neither
    /// <c>Job</c> nor the wire-level <c>JobStatus</c> persists
    /// <c>GitMirrorResult.Message</c>/<c>TransferPerformed</c> either. Per the
    /// plan's own fallback wording, this test asserts the minimum bar instead:
    /// <c>DoneSuccess</c> plus the destination's <c>for-each-ref</c> output
    /// byte-identical before and after the second run (which a real transfer,
    /// fast-forward or not, could not leave true only by accident).</para>
    /// </summary>
    [SkippableFact]
    public async Task GitPlusGitCopyRule_SecondRun_WhenSourceUnchanged_CompletesWithNoTransfer()
    {
        _fixture.SkipIfUnavailable();
        await _fixture.ResetAsync();

        await using var harness = await FullLoopHarness.StartAsync(_fixture);

        var sourceHost = Directory.CreateTempSubdirectory("backer-git-e2e-src2-").FullName;
        var destinationHost = Directory.CreateTempSubdirectory("backer-git-e2e-dst2-").FullName;

        var sourceBare = Path.Combine(sourceHost, "repo.git");
        GitFixtureRepo.InitBare(sourceBare);
        var work = Path.Combine(sourceHost, "work");
        GitFixtureRepo.CloneToWorkdir(sourceBare, work);
        GitFixtureRepo.CreateOrphanBranch(work, "main");
        var headSha = GitFixtureRepo.Commit(work, "a.txt", "hello from the second-run fast path test", "init");
        GitFixtureRepo.Push(work, "refs/heads/*:refs/heads/*");

        var destinationBare = Path.Combine(destinationHost, "repo.git");
        GitFixtureRepo.InitBare(destinationBare);

        var sourceStorageId = await _createGitStorageAsync(harness, "e2egitsrc2", sourceHost);
        var destinationStorageId = await _createGitStorageAsync(harness, "e2egitdst2", destinationHost);

        var sourceEndpointId = await _createEndpointAsync(harness, sourceStorageId, "repo.git");
        var destinationEndpointId = await _createEndpointAsync(harness, destinationStorageId, "repo.git");

        var ruleResponse = await harness.Client.PostAsJsonAsync(
            "/api/hannibal/v1/rules",
            new Rule
            {
                Name = "e2e git copy rule (second run)",
                SourceEndpointId = sourceEndpointId,
                DestinationEndpointId = destinationEndpointId,
                Operation = Rule.RuleOperation.Copy,
                MaxDestinationAge = TimeSpan.FromHours(1),
                MinRetryTime = TimeSpan.FromMinutes(15),
                MaxTimeAfterSourceModification = TimeSpan.FromMinutes(30),
                DailyTriggerTime = TimeSpan.FromHours(3)
            });

        ruleResponse.EnsureSuccessStatusCode();
        var rule = await ruleResponse.Content.ReadFromJsonAsync<CreateRuleResult>();

        var job1 = await harness.WaitForAsync(
            async context => await context.Jobs
                .Where(j => j.FromRuleId == rule!.Id && j.State == Job.JobState.DoneSuccess)
                .FirstOrDefaultAsync(),
            "the first git job to be reported as DoneSuccess",
            timeout: TimeSpan.FromSeconds(60));

        var destinationRefsAfterFirstRun = GitFixtureRepo.ForEachRef(destinationBare);
        destinationRefsAfterFirstRun.Should().ContainKey("refs/heads/main");
        destinationRefsAfterFirstRun["refs/heads/main"].Should().Be(headSha);

        // Seed the second Ready job directly - see the doc comment above for
        // why no REST trigger exists.
        var job2Id = await harness.WithContextAsync(async context =>
        {
            var ruleRow = await context.Rules
                .Include(r => r.SourceEndpoint).ThenInclude(e => e.Storage)
                .Include(r => r.DestinationEndpoint).ThenInclude(e => e.Storage)
                .FirstAsync(r => r.Id == rule!.Id);

            var now = DateTime.UtcNow;
            var job2 = new Job
            {
                UserId = job1.UserId,
                Tag = "e2e-git-second-run",
                Operation = Rule.RuleOperation.Copy,
                FromRule = ruleRow,
                Owner = "",
                State = Job.JobState.Ready,
                StartFrom = now.AddMinutes(-1),
                EndBy = now.AddDays(1),
                LastReported = now,
                SourceEndpoint = ruleRow.SourceEndpoint,
                DestinationEndpoint = ruleRow.DestinationEndpoint
            };
            context.Jobs.Add(job2);
            await context.SaveChangesAsync();
            return job2.Id;
        });

        // Nudge the agent the same way production does when a job becomes
        // available, instead of waiting on the 120s safety-net poll.
        using (var scope = harness.Api.Services.CreateScope())
        {
            var hub = scope.ServiceProvider.GetRequiredService<IHubContext<Hannibal.HannibalHub>>();
            await hub.Clients.All.SendAsync("NewJobAvailable");
        }

        var job2 = await harness.WaitForAsync(
            async context => await context.Jobs
                .Where(j => j.Id == job2Id && j.State == Job.JobState.DoneSuccess)
                .FirstOrDefaultAsync(),
            "the second git job (no-op fast path) to be reported as DoneSuccess",
            timeout: TimeSpan.FromSeconds(30));

        job2.Operation.Should().Be(Rule.RuleOperation.Copy);
        job2.Owner.Should().BeEmpty("ReportJobAsync releases the job when it completes");

        var destinationRefsAfterSecondRun = GitFixtureRepo.ForEachRef(destinationBare);
        destinationRefsAfterSecondRun.Should().BeEquivalentTo(destinationRefsAfterFirstRun,
            "the second run must not have transferred anything - the destination is byte-identical");
        destinationRefsAfterSecondRun["refs/heads/main"].Should().Be(headSha,
            "source and destination must still agree after the fast-path run");
    }

    private static async Task<int> _createGitStorageAsync(FullLoopHarness harness, string uriSchema, string host)
    {
        var response = await harness.Client.PostAsJsonAsync(
            "/api/hannibal/v1/storages",
            new Storage
            {
                Technology = "git",
                UriSchema = uriSchema,
                Host = host,
                IsActive = true
            });

        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreateStorageResult>();
        return created!.Id;
    }

    private static async Task<int> _createEndpointAsync(FullLoopHarness harness, int storageId, string path)
    {
        var response = await harness.Client.PostAsJsonAsync(
            "/api/hannibal/v1/endpoints",
            new Endpoint
            {
                Name = $"e2e-git-{storageId}-{path}",
                StorageId = storageId,
                Path = path,
                IsActive = true
            });

        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreateEndpointResult>();
        return created!.Id;
    }
}
