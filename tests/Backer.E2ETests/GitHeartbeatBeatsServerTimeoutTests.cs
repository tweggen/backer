using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Hannibal.Models;
using Microsoft.EntityFrameworkCore;
using TestSupport.Api;
using Xunit;

namespace Backer.E2ETests;

/// <summary>
/// Gate D AC8 (docs/plan-git-repo-storage.md): the git engine's heartbeat
/// (<c>GitWorkerService._runHeartbeatLoopAsync</c>, plan §6) keeps a
/// long-running job alive against the server's REAL 120-second timeout
/// contract (<c>HannibalServiceJobs.cs:205-222</c>, pinned by
/// <c>Hannibal.IntegrationTests/JobTimeoutContractTests.cs</c>).
///
/// <para>The agent's <c>GitWorker:GitPath</c> is pointed at a stub that
/// answers <c>--version</c> immediately but sleeps ~170s on any other
/// invocation. The engine's first real call for any job is
/// <c>git ls-remote</c> against the source - so the stub holds the job in
/// <c>Executing</c> for the entire sleep with zero real git activity, which
/// is exactly what proves the heartbeat (not the git process) is what keeps
/// <c>LastReported</c> fresh.</para>
///
/// <para>This test asserts the >120s mid-flight proof only, deliberately -
/// note the retry semantics discovered while building it:
/// <c>HannibalServiceJobs.ReportJobAsync</c> turns a reported
/// <c>DoneFailure</c> into <c>State=Ready, Owner=""</c> for retry
/// (<c>HannibalServiceJobs.cs:396-407</c>, the same "requeue on failure"
/// behaviour <c>FullLoopTests.AFailedTransferIsRequeuedAndRetried_NotSilentlyDropped</c>
/// documents for rclone) rather than persisting a terminal
/// <c>DoneFailure</c> row, and does so via an immediate <c>NewJobAvailable</c>
/// broadcast (<c>HannibalServiceJobs.cs:457</c>) that the same still-running
/// agent reacts to right away - so once the stub eventually exits, the job
/// cycles back to <c>Executing</c> too quickly for a polling assertion to
/// reliably observe the intermediate <c>Ready</c> row without being flaky.
/// The test therefore ends once the core contract is proven and lets the
/// harness's teardown cancel the still-sleeping stub - explicitly tolerated,
/// per the plan, as "the eventual job failure after the stub exits".</para>
///
/// <para><b>Runs ~2.5 minutes by design</b> (Gate D AC8) - this is not a
/// flaky sleep-based test, it is a bounded wait on the real clock the
/// server's timeout itself uses.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class GitHeartbeatBeatsServerTimeoutTests
{
    private const string AcquireRoute = "/api/hannibal/v1/acquireNextJob";

    private readonly PostgresFixture _fixture;

    public GitHeartbeatBeatsServerTimeoutTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task AC8_LongRunningGitJob_SurvivesThe120sServerTimeout_ViaHeartbeat()
    {
        _fixture.SkipIfUnavailable();
        await _fixture.ResetAsync();

        var stubDir = Directory.CreateTempSubdirectory("backer-git-ac8-").FullName;
        var stubPath = _writeSlowGitStub(stubDir);

        await using var harness = await FullLoopHarness.StartAsync(
            _fixture,
            configureGitWorkerOptions: options =>
            {
                options.GitPath = stubPath;

                // Named explicitly per the plan even though the defaults
                // (10 min stall, 2 h job) already clear the ~150s sleep -
                // the watchdog must not be the thing that ends this job.
                options.StallTimeout = TimeSpan.FromMinutes(5);
                options.JobTimeout = TimeSpan.FromMinutes(10);
            });

        var (sourceHost, destinationHost) = (
            Directory.CreateTempSubdirectory("backer-git-ac8-src-").FullName,
            Directory.CreateTempSubdirectory("backer-git-ac8-dst-").FullName);

        var ruleId = await _createGitCopyRuleAsync(harness, sourceHost, destinationHost);

        var executingJob = await harness.WaitForAsync(
            async context => await context.Jobs
                .Where(j => j.FromRuleId == ruleId && j.State == Job.JobState.Executing)
                .FirstOrDefaultAsync(),
            "the git job to be acquired and start executing",
            timeout: TimeSpan.FromSeconds(60));

        var jobId = executingJob.Id;
        var executingObservedAtUtc = DateTime.UtcNow;
        Console.WriteLine($"[AC8] job {jobId} observed Executing at {executingObservedAtUtc:O}");

        /*
         * Poll across the sleep window (deliberately shorter than the
         * stub's own ~170s sleep, so this loop never has to observe the
         * eventual DoneFailure transition mid-poll): the job must stay
         * Executing (never DoneFailure/timed out) and LastReported must
         * keep advancing - proof the heartbeat, not the stalled git stub,
         * is what is being reported. Bounded by wall clock, not a fixed
         * sleep.
         */
        var lastReportedSeen = new List<DateTime>();
        var pollDeadline = executingObservedAtUtc + TimeSpan.FromSeconds(130);
        while (DateTime.UtcNow < pollDeadline)
        {
            var row = await harness.WithContextAsync(
                async context => await context.Jobs.FindAsync(new object[] { jobId }));

            row.Should().NotBeNull();
            row!.State.Should().Be(Job.JobState.Executing,
                "the heartbeat must keep the job alive past the server's 120s timeout - " +
                "it must not be marked DoneFailure while the stub is still sleeping");

            if (lastReportedSeen.Count == 0 || row.LastReported != lastReportedSeen[^1])
            {
                lastReportedSeen.Add(row.LastReported);
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        lastReportedSeen.Should().HaveCountGreaterThan(1,
            "LastReported must have advanced more than once across the >120s window - " +
            "that is the server actually receiving repeated Executing reports (heartbeats), " +
            "not one report that merely happened to be recent enough");

        Console.WriteLine(
            $"[AC8] poll loop done at {DateTime.UtcNow:O}, elapsed {(DateTime.UtcNow - executingObservedAtUtc)}, " +
            $"{lastReportedSeen.Count} distinct LastReported value(s) seen");

        (DateTime.UtcNow - executingObservedAtUtc).Should().BeGreaterThan(TimeSpan.FromSeconds(125));

        /*
         * The real contract, exercised directly (mirrors
         * JobTimeoutContractTests.Fresh_executing_job_is_not_timed_out...):
         * a rival acquire call for the same capability, past the 120s mark,
         * must still be refused because the job's endpoints are still held -
         * proof the server did NOT time this job out and did NOT free it for
         * a second agent to acquire.
         */
        var rivalAcquire = await harness.Client.PostAsJsonAsync(AcquireRoute, new AcquireParams
        {
            Username = "",
            Capabilities = "git",
            Owner = "rival-git-agent",
            Networks = ""
        });
        rivalAcquire.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the original job's endpoints must still be held - a timed-out job would have freed them " +
            "for a rival agent to acquire, which must not happen while the heartbeat is beating");

        var stillOwnedRow = await harness.WithContextAsync(
            async context => await context.Jobs.FindAsync(new object[] { jobId }));
        stillOwnedRow!.State.Should().Be(Job.JobState.Executing);
        stillOwnedRow.Owner.Should().NotBeNullOrEmpty("the job must still be owned by the agent that acquired it");
        stillOwnedRow.Id.Should().Be(jobId, "the same job row throughout - it was never re-minted");

        var jobCountForRule = await harness.WithContextAsync(
            async context => await context.Jobs.Where(j => j.FromRuleId == ruleId).CountAsync());
        jobCountForRule.Should().Be(1,
            "no replacement job was minted for this rule while the original was held Executing");

        Console.WriteLine(
            $"[AC8] core proof complete at {DateTime.UtcNow:O}, " +
            $"total elapsed since Executing: {(DateTime.UtcNow - executingObservedAtUtc)} - " +
            "ending the test here; harness teardown will cancel the still-sleeping stub.");
    }

    private static async Task<int> _createGitCopyRuleAsync(
        FullLoopHarness harness, string sourceHost, string destinationHost)
    {
        var sourceStorageId = await _createGitStorageAsync(harness, "ac8gitsrc", sourceHost);
        var destinationStorageId = await _createGitStorageAsync(harness, "ac8gitdst", destinationHost);

        var sourceEndpointId = await _createEndpointAsync(harness, sourceStorageId, "source-repo");
        var destinationEndpointId = await _createEndpointAsync(harness, destinationStorageId, "dest-repo");

        var response = await harness.Client.PostAsJsonAsync(
            "/api/hannibal/v1/rules",
            new Rule
            {
                Name = "ac8-git-heartbeat-rule",
                SourceEndpointId = sourceEndpointId,
                DestinationEndpointId = destinationEndpointId,
                Operation = Rule.RuleOperation.Copy,
                MaxDestinationAge = TimeSpan.FromHours(1),
                MinRetryTime = TimeSpan.FromHours(1),
                MaxTimeAfterSourceModification = TimeSpan.FromMinutes(30),
                DailyTriggerTime = TimeSpan.FromHours(3)
            });

        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreateRuleResult>();
        return created!.Id;
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
                Name = $"ac8-{storageId}-{path}",
                StorageId = storageId,
                Path = path,
                IsActive = true
            });

        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreateEndpointResult>();
        return created!.Id;
    }

    /// <summary>
    /// A .cmd, same technique as <c>WorkerGit.Tests/GitWatchdogTests</c>'s
    /// stalling stub: answers <c>--version</c> immediately (so the agent's
    /// startup probe and capability advertisement succeed), sleeps ~170s
    /// (measured: Windows' <c>ping -n N</c> takes almost exactly N seconds
    /// on this host) on anything else, then exits 0 with no output - which
    /// is exactly what an empty <c>ls-remote</c> looks like to the engine.
    /// Deliberately longer than the poll loop's own 130s window below, so
    /// that loop never has to observe the eventual DoneFailure transition
    /// mid-poll.
    /// </summary>
    private static string _writeSlowGitStub(string dir)
    {
        var path = Path.Combine(dir, "git-stub-ac8.cmd");
        File.WriteAllText(
            path,
            "@echo off\r\n"
            + "if \"%~1\"==\"--version\" (\r\n"
            + "    echo git version 2.49.0\r\n"
            + "    exit /b 0\r\n"
            + ")\r\n"
            + "ping -n 170 127.0.0.1 >nul\r\n"
            + "exit /b 0\r\n");
        return path;
    }
}
