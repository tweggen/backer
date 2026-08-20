using FluentAssertions;
using TestSupport.RClone;
using WorkerRClone.Client;
using WorkerRClone.Client.Models;
using Xunit;

namespace WorkerRClone.Tests.Client;

/// <summary>
/// Gate 1 of <c>docs/plan-e2e-test-harness.md</c>: proves <see cref="RCloneStub"/>
/// is a usable stand-in for rclone's remote-control API.
///
/// <para>Every test here drives the <b>real</b> <see cref="RCloneClient"/> - the
/// same class the agent uses - against the stub. The stub writes its JSON by
/// hand and never references the client's model types, so a passing test means
/// the two independently agree on the wire format. Serialising the client's own
/// models in the stub would have made these assertions circular.</para>
///
/// <para>What these tests do <b>not</b> prove is that the stub matches a real
/// rclone binary; see the fidelity caveat on <see cref="RCloneStub"/>.</para>
/// </summary>
public class RCloneStubContractTests
{
    /// <summary>
    /// The client only ever throws a bare <see cref="Exception"/> carrying the
    /// response body, so tests assert on the message rather than a type.
    /// </summary>
    private static async Task<Exception> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception e)
        {
            return e;
        }

        throw new Xunit.Sdk.XunitException("Expected the call to throw, but it succeeded.");
    }

    // ------------------------------------------------------------------
    // AC1 / AC2 - every endpoint the client calls answers, and the client
    // deserialises each answer correctly.
    // ------------------------------------------------------------------

    [Fact]
    public async Task ListRemotes_ReturnsRemotesCreatedThroughConfigCreate()
    {
        await using var stub = await RCloneStub.StartAsync();
        var client = new RCloneClient(stub.CreateClient());

        var before = await client.ListRemotesAsync(CancellationToken.None);
        before.remotes.Should().BeEmpty();

        await client.CreateConfigAsync(
            "onedrive",
            "my-onedrive",
            new SortedDictionary<string, string> { ["token"] = "abc", ["drive_type"] = "personal" },
            new RemoteOptions(),
            CancellationToken.None);

        var after = await client.ListRemotesAsync(CancellationToken.None);
        after.remotes.Should().ContainSingle().Which.Should().Be("my-onedrive");
    }

    [Fact]
    public async Task CreateConfig_RecordsTheParametersThatWouldReachRcloneConf()
    {
        await using var stub = await RCloneStub.StartAsync();
        var client = new RCloneClient(stub.CreateClient());

        await client.CreateConfigAsync(
            "onedrive",
            "my-onedrive",
            new SortedDictionary<string, string> { ["token"] = "abc", ["drive_type"] = "personal" },
            new RemoteOptions(),
            CancellationToken.None);

        var parameters = stub.GetRemoteParameters("my-onedrive");

        parameters.Should().NotBeNull();
        parameters!["token"]!.GetValue<string>().Should().Be("abc");
        parameters["drive_type"]!.GetValue<string>().Should().Be("personal");
        stub.GetRemoteParameters("never-created").Should().BeNull();
    }

    [Fact]
    public async Task GetPaths_Deserialises()
    {
        await using var stub = await RCloneStub.StartAsync();
        var client = new RCloneClient(stub.CreateClient());

        var paths = await client.GetPathsAsync(CancellationToken.None);

        paths.config.Should().NotBeNullOrEmpty();
        paths.cache.Should().NotBeNullOrEmpty();
        paths.temp.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Copy_ReturnsAJobId_AndTheJobCompletes()
    {
        await using var stub = await RCloneStub.StartAsync();
        var client = new RCloneClient(stub.CreateClient());

        var started = await client.CopyAsync("src:/from", "dst:/to", CancellationToken.None);
        started.jobid.Should().BeGreaterThan(0);

        var status = await client.GetJobStatusAsync(started.jobid, CancellationToken.None);

        status.id.Should().Be(started.jobid);
        status.finished.Should().BeTrue();
        status.success.Should().BeTrue();
        status.error.Should().BeEmpty();
    }

    [Fact]
    public async Task Sync_And_Noop_AlsoReturnJobIds()
    {
        await using var stub = await RCloneStub.StartAsync();
        var client = new RCloneClient(stub.CreateClient());

        var sync = await client.SyncAsync("src:/from", "dst:/to", CancellationToken.None);
        var noop = await client.NoopAsync(CancellationToken.None);

        sync.jobid.Should().BeGreaterThan(0);
        noop.jobid.Should().BeGreaterThan(0);
        noop.jobid.Should().NotBe(sync.jobid);
    }

    [Fact]
    public async Task JobList_SeparatesRunningFromFinished()
    {
        await using var stub = await RCloneStub.StartAsync();
        var client = new RCloneClient(stub.CreateClient());

        stub.Script.EnqueueStall();
        var stalled = await client.CopyAsync("src:/a", "dst:/a", CancellationToken.None);

        var finished = await client.CopyAsync("src:/b", "dst:/b", CancellationToken.None);
        await client.GetJobStatusAsync(finished.jobid, CancellationToken.None);

        var list = await client.GetJobListAsync(CancellationToken.None);

        list.executeId.Should().NotBeNullOrEmpty();
        list.running_ids.Should().Contain(stalled.jobid);
        list.finished_ids.Should().Contain(finished.jobid);
        list.jobsids.Should().Contain(new[] { stalled.jobid, finished.jobid });
    }

    [Fact]
    public async Task Stats_ReturnWhatTheTestScripted()
    {
        await using var stub = await RCloneStub.StartAsync();
        var client = new RCloneClient(stub.CreateClient());

        stub.Stats.Bytes = 1_234;
        stub.Stats.TotalBytes = 4_096;
        stub.Stats.Transfers = 2;
        stub.Stats.Errors = 1;
        stub.Stats.LastError = "one file failed";
        stub.Stats.Speed = 512.5;
        stub.Stats.Transferring.Add(new StubTransferringItem
        {
            Name = "holiday.jpg",
            Bytes = 100,
            Size = 200,
            Percentage = 50,
            Group = "job/1"
        });

        var stats = await client.GetJobStatsAsync(CancellationToken.None);

        stats.bytes.Should().Be(1_234);
        stats.totalBytes.Should().Be(4_096);
        stats.transfers.Should().Be(2);
        stats.errors.Should().Be(1);
        stats.lastError.Should().Be("one file failed");
        stats.speed.Should().Be(512.5);
        stats.transferring.Should().ContainSingle();
        stats.transferring![0].name.Should().Be("holiday.jpg");
        stats.transferring[0].percentage.Should().Be(50);
        stats.transferring[0].group.Should().Be("job/1");
    }

    [Fact]
    public async Task Stats_ForOneGroup_UseTheSameShape()
    {
        await using var stub = await RCloneStub.StartAsync();
        var client = new RCloneClient(stub.CreateClient());

        stub.Stats.Bytes = 77;

        var stats = await client.GetJobStatsAsync("job/1", CancellationToken.None);

        stats.bytes.Should().Be(77);
        stub.RequestsFor("/core/stats").Should().ContainSingle()
            .Which.GetString("group").Should().Be("job/1");
    }

    [Fact]
    public async Task Quit_IsObserved()
    {
        await using var stub = await RCloneStub.StartAsync();
        var client = new RCloneClient(stub.CreateClient());

        stub.QuitRequested.Should().BeFalse();

        await client.Quit(CancellationToken.None);

        stub.QuitRequested.Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // AC1 - authentication
    // ------------------------------------------------------------------

    [Fact]
    public async Task RequestsWithoutTheAgentsBasicAuth_AreRejected()
    {
        await using var stub = await RCloneStub.StartAsync();

        using var anonymous = new HttpClient { BaseAddress = new Uri(stub.BaseAddress) };
        var client = new RCloneClient(anonymous);

        var exception = await CaptureAsync(() => client.ListRemotesAsync(CancellationToken.None));

        exception.Message.Should().Contain("unauthorized");
    }

    [Fact]
    public async Task TheAcceptedCredentialsAreTheOnesTheAgentHardcodes()
    {
        /*
         * RCloneService builds its basic auth header from the literal
         * "who:how" (RCloneService.cs:1036-1039). If that ever changes, this
         * test is the one that should fail.
         */
        RCloneStub.DefaultUsername.Should().Be("who");
        RCloneStub.DefaultPassword.Should().Be("how");

        await using var stub = await RCloneStub.StartAsync(username: "someone", password: "else");

        using var wrong = new HttpClient { BaseAddress = new Uri(stub.BaseAddress) };
        wrong.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization",
            "Basic " + Convert.ToBase64String("who:how"u8.ToArray()));

        var client = new RCloneClient(wrong);
        var exception = await CaptureAsync(() => client.ListRemotesAsync(CancellationToken.None));

        exception.Message.Should().Contain("unauthorized");
    }

    // ------------------------------------------------------------------
    // AC3 - scripted behaviours
    // ------------------------------------------------------------------

    [Fact]
    public async Task ScriptedFailure_SurfacesAsAFinishedUnsuccessfulJob()
    {
        await using var stub = await RCloneStub.StartAsync();
        var client = new RCloneClient(stub.CreateClient());

        stub.Script.EnqueueFailure("directory not found");

        var started = await client.CopyAsync("src:/from", "dst:/to", CancellationToken.None);
        var status = await client.GetJobStatusAsync(started.jobid, CancellationToken.None);

        status.finished.Should().BeTrue();
        status.success.Should().BeFalse();
        status.error.Should().Be("directory not found");
    }

    [Fact]
    public async Task ScriptedSlowSuccess_ReportsRunningForTheScriptedNumberOfPolls()
    {
        await using var stub = await RCloneStub.StartAsync();
        var client = new RCloneClient(stub.CreateClient());

        stub.Script.EnqueueSlowSuccess(polls: 2);

        var started = await client.CopyAsync("src:/from", "dst:/to", CancellationToken.None);

        (await client.GetJobStatusAsync(started.jobid, CancellationToken.None)).finished.Should().BeFalse();
        (await client.GetJobStatusAsync(started.jobid, CancellationToken.None)).finished.Should().BeFalse();

        var third = await client.GetJobStatusAsync(started.jobid, CancellationToken.None);
        third.finished.Should().BeTrue();
        third.success.Should().BeTrue();
    }

    [Fact]
    public async Task ScriptedStall_NeverFinishes()
    {
        await using var stub = await RCloneStub.StartAsync();
        var client = new RCloneClient(stub.CreateClient());

        stub.Script.EnqueueStall();

        var started = await client.CopyAsync("src:/from", "dst:/to", CancellationToken.None);

        for (var i = 0; i < 5; i++)
        {
            var status = await client.GetJobStatusAsync(started.jobid, CancellationToken.None);
            status.finished.Should().BeFalse();
        }
    }

    [Fact]
    public async Task ScriptedBehavioursApplyInOrder_AndThenFallBackToTheDefault()
    {
        await using var stub = await RCloneStub.StartAsync();
        var client = new RCloneClient(stub.CreateClient());

        stub.Script.EnqueueFailure("first fails");
        stub.Script.EnqueueStall();

        var first = await client.CopyAsync("src:/1", "dst:/1", CancellationToken.None);
        var second = await client.CopyAsync("src:/2", "dst:/2", CancellationToken.None);
        var third = await client.CopyAsync("src:/3", "dst:/3", CancellationToken.None);

        (await client.GetJobStatusAsync(first.jobid, CancellationToken.None)).error
            .Should().Be("first fails");
        (await client.GetJobStatusAsync(second.jobid, CancellationToken.None)).finished
            .Should().BeFalse();
        (await client.GetJobStatusAsync(third.jobid, CancellationToken.None)).success
            .Should().BeTrue();
    }

    [Fact]
    public async Task StoppingAJob_MarksItFinishedAndUnsuccessful()
    {
        await using var stub = await RCloneStub.StartAsync();
        var client = new RCloneClient(stub.CreateClient());

        stub.Script.EnqueueStall();
        var started = await client.CopyAsync("src:/from", "dst:/to", CancellationToken.None);

        await client.StopJobAsync(started.jobid, CancellationToken.None);

        var status = await client.GetJobStatusAsync(started.jobid, CancellationToken.None);
        status.finished.Should().BeTrue();
        status.success.Should().BeFalse();
        status.error.Should().Be("context canceled");
    }

    [Fact]
    public async Task AnUnknownJobId_ProducesAnError()
    {
        await using var stub = await RCloneStub.StartAsync();
        var client = new RCloneClient(stub.CreateClient());

        var exception = await CaptureAsync(
            () => client.GetJobStatusAsync(4242, CancellationToken.None));

        exception.Message.Should().Contain("job not found");
    }

    // ------------------------------------------------------------------
    // AC4 - request recording
    // ------------------------------------------------------------------

    [Fact]
    public async Task TheCopyRequestRecordsTheSourceAndDestinationUris()
    {
        await using var stub = await RCloneStub.StartAsync();
        var client = new RCloneClient(stub.CreateClient());

        await client.CopyAsync("onedrive:/Backups/Daily", "nas:/DATA/Backups", CancellationToken.None);

        var recorded = await stub.WaitForRequestAsync("/sync/copy");

        recorded.GetString("srcFs").Should().Be("onedrive:/Backups/Daily");
        recorded.GetString("dstFs").Should().Be("nas:/DATA/Backups");
    }

    [Fact]
    public async Task WaitForRequest_ReturnsRequestsThatAlreadyArrived()
    {
        await using var stub = await RCloneStub.StartAsync();
        var client = new RCloneClient(stub.CreateClient());

        await client.CopyAsync("src:/1", "dst:/1", CancellationToken.None);
        await client.CopyAsync("src:/2", "dst:/2", CancellationToken.None);

        var second = await stub.WaitForRequestAsync("/sync/copy", count: 2);

        second.GetString("srcFs").Should().Be("src:/2");
    }

    [Fact]
    public async Task WaitForRequest_CompletesWhenTheRequestArrivesLater()
    {
        await using var stub = await RCloneStub.StartAsync();
        var client = new RCloneClient(stub.CreateClient());

        var waiting = stub.WaitForRequestAsync("/sync/sync");
        waiting.IsCompleted.Should().BeFalse();

        await client.SyncAsync("src:/late", "dst:/late", CancellationToken.None);

        var recorded = await waiting;
        recorded.GetString("srcFs").Should().Be("src:/late");
    }

    [Fact]
    public async Task WaitForRequest_TimesOutWithAUsefulMessage()
    {
        await using var stub = await RCloneStub.StartAsync();
        var client = new RCloneClient(stub.CreateClient());

        await client.ListRemotesAsync(CancellationToken.None);

        var act = async () => await stub.WaitForRequestAsync(
            "/sync/copy", timeout: TimeSpan.FromMilliseconds(200));

        (await act.Should().ThrowAsync<TimeoutException>())
            .Which.Message.Should().Contain("/config/listremotes");
    }

    // ------------------------------------------------------------------
    // AC5 - two stubs at once
    // ------------------------------------------------------------------

    [Fact]
    public async Task TwoStubsRunConcurrentlyOnDifferentPorts()
    {
        await using var first = await RCloneStub.StartAsync();
        await using var second = await RCloneStub.StartAsync();

        first.BaseAddress.Should().NotBe(second.BaseAddress);

        var firstClient = new RCloneClient(first.CreateClient());
        var secondClient = new RCloneClient(second.CreateClient());

        await firstClient.CreateConfigAsync(
            "local", "only-on-first", new SortedDictionary<string, string>(),
            new RemoteOptions(), CancellationToken.None);

        (await firstClient.ListRemotesAsync(CancellationToken.None)).remotes
            .Should().ContainSingle().Which.Should().Be("only-on-first");
        (await secondClient.ListRemotesAsync(CancellationToken.None)).remotes
            .Should().BeEmpty();
    }
}
