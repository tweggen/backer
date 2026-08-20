using Microsoft.AspNetCore.SignalR.Client;
using System.Net.Http.Json;
using FluentAssertions;
using Hannibal.Models;
using Microsoft.EntityFrameworkCore;
using TestSupport.Api;
using Xunit;

namespace Backer.E2ETests;

/// <summary>
/// Gate 3 acceptance: a rule created over REST becomes a job, the agent
/// acquires it, rclone is asked to do the transfer, and the result lands back
/// in PostgreSQL - all asserted on real rows and real recorded RC calls.
/// </summary>
[Collection(PostgresCollection.Name)]
public class FullLoopTests
{
    private readonly PostgresFixture _fixture;

    public FullLoopTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<FullLoopHarness> ArrangeAsync()
    {
        _fixture.SkipIfUnavailable();
        await _fixture.ResetAsync();
        return await FullLoopHarness.StartAsync(_fixture);
    }

    /// <summary>
    /// Creates two local storages, an endpoint on each, and a rule between
    /// them. Returns the rule id.
    /// </summary>
    private static async Task<int> CreateCopyRuleAsync(
        FullLoopHarness harness,
        string sourcePath = "Documents/Work",
        string destinationPath = "Backups/Daily")
    {
        var source = await CreateStorageAsync(harness, "e2esource");
        var destination = await CreateStorageAsync(harness, "e2edest");

        var sourceEndpoint = await CreateEndpointAsync(harness, source, sourcePath);
        var destinationEndpoint = await CreateEndpointAsync(harness, destination, destinationPath);

        var response = await harness.Client.PostAsJsonAsync(
            "/api/hannibal/v1/rules",
            new Rule
            {
                Name = "e2e copy rule",
                SourceEndpointId = sourceEndpoint,
                DestinationEndpointId = destinationEndpoint,
                Operation = Rule.RuleOperation.Copy,
                MaxDestinationAge = TimeSpan.FromHours(1),
                MinRetryTime = TimeSpan.FromMinutes(15),
                MaxTimeAfterSourceModification = TimeSpan.FromMinutes(30),
                DailyTriggerTime = TimeSpan.FromHours(3)
            });

        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreateRuleResult>();
        return created!.Id;
    }

    private static async Task<int> CreateStorageAsync(FullLoopHarness harness, string uriSchema)
    {
        var response = await harness.Client.PostAsJsonAsync(
            "/api/hannibal/v1/storages",
            new Storage
            {
                Technology = "local",
                UriSchema = uriSchema,
                IsActive = true
            });

        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreateStorageResult>();
        return created!.Id;
    }

    private static async Task<int> CreateEndpointAsync(
        FullLoopHarness harness, int storageId, string path)
    {
        var response = await harness.Client.PostAsJsonAsync(
            "/api/hannibal/v1/endpoints",
            new Endpoint
            {
                Name = $"e2e-{storageId}-{path.Replace('/', '-')}",
                StorageId = storageId,
                Path = path,
                IsActive = true
            });

        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreateEndpointResult>();
        return created!.Id;
    }

    [SkippableFact]
    public async Task ARuleCreatedOverRest_BecomesAJobTheAgentRunsAndReports()
    {
        await using var harness = await ArrangeAsync();

        var ruleId = await CreateCopyRuleAsync(harness);

        /*
         * The scheduler creates the job; the agent acquires it and asks rclone
         * to copy. The recorded RC call is the contract between Backer and
         * rclone, so it is asserted directly.
         */
        var copy = await harness.Stub.WaitForRequestAsync(
            "/sync/copy", timeout: TimeSpan.FromSeconds(60));

        copy.GetString("srcFs").Should().Be("e2esource:/Documents/Work");
        copy.GetString("dstFs").Should().Be("e2edest:/Backups/Daily");

        var job = await harness.WaitForAsync(
            async context => await context.Jobs
                .Where(j => j.FromRuleId == ruleId && j.State == Job.JobState.DoneSuccess)
                .FirstOrDefaultAsync(),
            "the job to be reported as DoneSuccess");

        job.Operation.Should().Be(Rule.RuleOperation.Copy);
        job.Owner.Should().BeEmpty(
            "ReportJobAsync releases the job when it completes (HannibalServiceJobs.cs:376-377)");
    }

    /// <summary>
    /// The first test in the codebase to exercise SignalR end to end rather
    /// than against a recording fake: the API runs its real hub
    /// (<c>Api/Program.cs:167</c>) and the agent genuinely connects to it.
    ///
    /// <para>This is how the agent hears about work promptly. Its own polling
    /// safety net only fires every 120 seconds
    /// (<c>RCloneService.cs:64</c>).</para>
    /// </summary>
    [SkippableFact]
    public async Task TheAgentIsConnectedToTheRealSignalRHub()
    {
        await using var harness = await ArrangeAsync();

        await harness.Agent.WaitForHubStateAsync(HubConnectionState.Connected);

        harness.Agent.HannibalConnection.State.Should().Be(HubConnectionState.Connected);

        /*
         * And the notification actually moves work: the copy must arrive well
         * inside the 120-second poll interval, so it cannot have come from the
         * safety net.
         */
        await CreateCopyRuleAsync(harness);

        await harness.Stub.WaitForRequestAsync("/sync/copy", timeout: TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// How a storage reaches rclone: the agent writes <c>backer-rclone.conf</c>
    /// itself (<c>RCloneService.cs:691</c>) and never calls the RC
    /// <c>config/create</c>. The section name is the storage's
    /// <c>UriSchema</c>, which is what makes the <c>remote:/path</c> URIs
    /// resolve.
    /// </summary>
    [SkippableFact]
    public async Task EachStorageBecomesASectionOfTheRcloneConfigFile()
    {
        await using var harness = await ArrangeAsync();

        await CreateCopyRuleAsync(harness);

        await harness.Stub.WaitForRequestAsync("/sync/copy", timeout: TimeSpan.FromSeconds(60));

        var configFile = Path.Combine(
            harness.Agent.RCloneConfigDirectory, "backer-rclone.conf");
        var config = await File.ReadAllTextAsync(configFile);

        config.Should().Contain("[e2esource]").And.Contain("[e2edest]");
        config.Should().Contain("type = local", "the local provider declares type=local");

        harness.Stub.RequestsFor("/config/create").Should().BeEmpty(
            "the agent configures rclone through the file, not the RC API");
    }

    /// <summary>
    /// A failed transfer is reported and the job goes back into the queue:
    /// <c>ReportJobAsync</c> turns a reported <c>DoneFailure</c> into
    /// <c>Ready</c> with no owner rather than recording the failure
    /// (<c>HannibalServiceJobs.cs:361-372</c>), so the work is retried.
    ///
    /// <para>Worth knowing: nothing counts the retries, so a job that always
    /// fails is retried indefinitely - the TXWTODO at that line says as
    /// much. Here only the first attempt is scripted to fail, so the second
    /// succeeds.</para>
    /// </summary>
    [SkippableFact]
    public async Task AFailedTransferIsRequeuedAndRetried_NotSilentlyDropped()
    {
        await using var harness = await ArrangeAsync();

        harness.Stub.Script.EnqueueFailure("directory not found");

        var ruleId = await CreateCopyRuleAsync(harness);

        /*
         * A second copy for the same rule is the observable proof that the
         * first was reported as failed and the job put back on the queue.
         */
        await harness.Stub.WaitForRequestAsync(
            "/sync/copy", count: 2, timeout: TimeSpan.FromSeconds(60));

        var job = await harness.WaitForAsync(
            async context => await context.Jobs
                .Where(j => j.FromRuleId == ruleId && j.State == Job.JobState.DoneSuccess)
                .FirstOrDefaultAsync(),
            "the retried job to succeed");

        job.Should().NotBeNull();
    }
}
