using System.Net.Http.Json;
using Backer.E2ETests.TestSupport;
using FluentAssertions;
using Hannibal.Models;
using Microsoft.EntityFrameworkCore;
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
