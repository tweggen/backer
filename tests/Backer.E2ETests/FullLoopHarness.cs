using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using System.Net.Http.Headers;
using Hannibal.Client.Configuration;
using Hannibal.Data;
using Microsoft.Extensions.DependencyInjection;
using TestSupport.Agent;
using TestSupport.Api;
using TestSupport.RClone;
using WorkerGit.Configuration;
using WorkerRClone.Models;

namespace Backer.E2ETests;

/// <summary>
/// Gate 3 of <c>docs/plan-e2e-test-harness.md</c>: the real API and the real
/// agent in one process, joined by the API's <c>TestServer</c> handler, with a
/// stubbed rclone at the far end.
///
/// <code>
/// HTTP ─▶ Api ─▶ PostgreSQL
///          │
///          └─(RuleScheduler)─▶ Job ─▶ agent acquires ─▶ stub rclone
///                                          │
///                                          └─▶ reports ─▶ PostgreSQL
/// </code>
///
/// <para>The agent keeps its production client registration - including
/// <c>AutoAuthHandler</c> - and only its transport is redirected at the
/// in-memory server, so it registers, authenticates and polls for real. That is
/// the point of this layer: everything between the HTTP call and the rclone RC
/// call is the shipping code.</para>
///
/// <para>A host per test, rather than one shared for the run: the scheduler is
/// live here, and it caches rule state that a database reset would silently
/// invalidate.</para>
/// </summary>
public sealed class FullLoopHarness : IAsyncDisposable
{
    /// <summary>
    /// The API is reached over https because <c>Api/Program.cs</c> calls
    /// <c>UseHttpsRedirection()</c>; an http base address would earn a 307.
    /// </summary>
    private const string ApiBaseUrl = "https://localhost/";

    private FullLoopHarness(BackerApiFactory api, AgentHostFactory agent, string userEmail)
    {
        Api = api;
        Agent = agent;
        UserEmail = userEmail;
    }

    public BackerApiFactory Api { get; }

    public AgentHostFactory Agent { get; }

    /// <summary>The rclone stand-in the agent talks to.</summary>
    public RCloneStub Stub => Agent.Stub;

    /// <summary>The identity both the REST calls and the agent use.</summary>
    public string UserEmail { get; }

    /// <summary>An API client carrying a bearer token for <see cref="UserEmail"/>.</summary>
    public HttpClient Client { get; private set; } = null!;

    /// <summary>
    /// <paramref name="configureGitWorkerOptions"/> is applied after the
    /// agent's own GitWorker defaults (a per-test cache directory, real
    /// "git" on PATH) - Gate D AC8's heartbeat test uses it to point
    /// <c>GitWorker:GitPath</c> at a stub that holds a job in flight well
    /// past the server's real 120s timeout.
    /// </summary>
    public static async Task<FullLoopHarness> StartAsync(
        PostgresFixture fixture, Action<GitWorkerOptions>? configureGitWorkerOptions = null)
    {
        /*
         * SchedulerMode.Enabled is what makes a Rule turn into a Job; HubMode.Real
         * so the agent's SignalR notifications are genuinely delivered rather
         * than merely recorded.
         */
        var api = new BackerApiFactory(fixture, SchedulerMode.Enabled, HubMode.Real);

        var email = $"agent-{Guid.NewGuid():N}@example.invalid";
        const string password = "Full-Loop-Password-1!";

        /*
         * Touching Services builds the host, which starts the scheduler. The
         * user has to exist before the agent's first login attempt.
         */
        _ = api.Services;
        var token = await api.RegisterAndGetTokenAsync(email, password);

        var agent = await AgentHostFactory.CreateAsync(new AgentHostOptions
        {
            HannibalClient = HannibalClientMode.Real,

            /*
             * A genuine SignalR connection over the TestServer. Long polling
             * rather than WebSockets: TestServer supports it without extra
             * plumbing, and the transport is not what is under test.
             *
             * This is load bearing, not decoration. Without it the agent only
             * learns about work on its 120-second safety-net poll, so anything
             * that requeues a job - a failed transfer, for instance - would take
             * two minutes to be picked up again.
             */
            HannibalHubConnection = () => new HubConnectionBuilder()
                .WithUrl($"{ApiBaseUrl}hannibal", options =>
                {
                    options.HttpMessageHandlerFactory = _ => api.Server.CreateHandler();
                    options.Transports = HttpTransportType.LongPolling;
                })
                .Build(),

            ConfigureRCloneOptions = options =>
            {
                options.BackerUsername = email;
                options.BackerPassword = password;
            },
            ConfigureGitWorkerOptions = configureGitWorkerOptions,
            ConfigureServices = services =>
            {
                services.PostConfigure<HannibalServiceClientOptions>(
                    options => options.BaseUrl = ApiBaseUrl);
                services.PostConfigure<IdentityApiServiceOptions>(
                    options => options.BaseUrl = ApiBaseUrl);

                /*
                 * Typed clients are named after their interface, so naming them
                 * again here appends to the existing registration rather than
                 * replacing it: the AutoAuthHandler stays in the chain and only
                 * the transport underneath it changes.
                 */
                services.AddHttpClient(nameof(Hannibal.Client.IHannibalServiceClient))
                    .ConfigurePrimaryHttpMessageHandler(() => api.Server.CreateHandler());
                services.AddHttpClient(nameof(Hannibal.Client.IIdentityApiService))
                    .ConfigurePrimaryHttpMessageHandler(() => api.Server.CreateHandler());
            }
        });

        var harness = new FullLoopHarness(api, agent, email);

        harness.Client = api.CreateApiClient();
        harness.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        await agent.StartAgentAsync();

        return harness;
    }

    /// <summary>The state the agent's rclone service is in right now.</summary>
    public RCloneServiceState.ServiceState AgentState => Agent.CurrentState;

    /// <summary>
    /// Runs <paramref name="work"/> against the same database the API writes
    /// to, so assertions are on real rows.
    /// </summary>
    public Task<T> WithContextAsync<T>(Func<HannibalContext, Task<T>> work) =>
        Api.WithContextAsync(work);

    /// <summary>
    /// Polls <paramref name="condition"/> against the database until it holds.
    /// A ceiling on a condition, not a sleep - it returns as soon as the
    /// condition is true, and names what it last saw when it does not.
    /// </summary>
    public async Task<T> WaitForAsync<T>(
        Func<HannibalContext, Task<T?>> condition,
        string describe,
        TimeSpan? timeout = null)
        where T : class
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));

        while (DateTime.UtcNow < deadline)
        {
            var found = await WithContextAsync(condition);
            if (found is not null)
            {
                return found;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Timed out waiting for {describe}.");
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();

        /*
         * The agent first: it polls the API, and tearing the API down under it
         * produces noisy failures on the way out.
         */
        Agent.Dispose();
        await Api.DisposeAsync();
    }
}
