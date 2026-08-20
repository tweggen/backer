using Hannibal.Client;
using Hannibal.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TestSupport.RClone;
using Tools;
using WorkerRClone.Configuration;
using WorkerRClone.Models;
using WorkerRClone.Services;

namespace BackerAgent.IntegrationTests;

/// <summary>
/// Gate 2 of <c>docs/plan-e2e-test-harness.md</c>: hosts the real
/// <c>BackerAgent</c> in-process, wired to an <see cref="RCloneStub"/> instead
/// of an rclone process and to a substitute Hannibal client instead of a
/// server.
///
/// <para>Everything that would reach the network, spawn a process, or read the
/// developer's own configuration is replaced:</para>
/// <list type="bullet">
/// <item><c>RCloneService:RCloneUrl</c> points at the stub and
/// <c>SkipProcessStart</c> is set, so no rclone binary can ever be
/// launched;</item>
/// <item><see cref="IHannibalServiceClient"/> is an NSubstitute double - the
/// agent never talks to an API;</item>
/// <item><see cref="ConfigHelper{TOptions}"/> is rebuilt against a throwaway
/// directory, because the production registration in
/// <c>BackerAgent/Program.cs:111-135</c> layers in user secrets and machine
/// config files. Without this a test run would load the real OneDrive and
/// Dropbox client secrets of whoever runs it;</item>
/// <item><c>HubConnectionService</c> is dropped so nothing tries to open a
/// SignalR connection. The connection object itself stays, because
/// <c>RCloneService</c> resolves <c>connections["hannibal"]</c> in its
/// constructor (<c>RCloneService.cs:145</c>) and only ever subscribes to it.
/// </item>
/// </list>
/// </summary>
public sealed class AgentHostFactory : WebApplicationFactory<BackerAgentHost>
{
    private readonly string _configDirectory =
        Path.Combine(Path.GetTempPath(), "backer-agent-tests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Throwaway directory this agent writes <c>backer-rclone.conf</c> into.
    /// Per factory, so hosts running in parallel cannot race on one file.
    /// </summary>
    public string RCloneConfigDirectory => Path.Combine(_configDirectory, "rclone");

    private readonly Action<RCloneServiceOptions>? _configureOptions;

    private AgentHostFactory(RCloneStub stub, Action<RCloneServiceOptions>? configureOptions)
    {
        Stub = stub;
        _configureOptions = configureOptions;
    }

    /// <summary>The rclone stand-in this agent talks to.</summary>
    public RCloneStub Stub { get; }

    /// <summary>The Hannibal API double. Configure it before touching <see cref="Services"/>.</summary>
    public IHannibalServiceClient Hannibal { get; } = Substitute.For<IHannibalServiceClient>();

    /// <summary>
    /// Starts a stub and builds an agent host pointed at it. The agent is not
    /// running yet - the host is built lazily on first access to
    /// <see cref="Services"/>, which is what <see cref="StartAgentAsync"/> does.
    /// </summary>
    /// <param name="configureOptions">
    /// Applied last, after the harness defaults, so a test can deviate from
    /// them - for instance to point the agent at a port nothing listens on.
    /// </param>
    public static async Task<AgentHostFactory> CreateAsync(
        Action<RCloneServiceOptions>? configureOptions = null)
    {
        var stub = await RCloneStub.StartAsync();
        var factory = new AgentHostFactory(stub, configureOptions);

        /*
         * A user must exist or _checkOnlineImpl treats it as an authentication
         * failure and parks in WaitConfig (RCloneService.cs:1104-1116).
         */
        factory.Hannibal
            .GetUserAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new Microsoft.AspNetCore.Identity.IdentityUser("agent-test-user"));

        factory.Hannibal
            .GetStoragesAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Storage>().AsEnumerable());

        factory.Hannibal
            .AcquireNextJobAsync(Arg.Any<AcquireParams>(), Arg.Any<CancellationToken>())
            .Returns((Job?)null);

        return factory;
    }

    /// <summary>
    /// Builds the host and waits for the service to reach
    /// <see cref="RCloneServiceState.ServiceState.Running"/>.
    /// </summary>
    public async Task StartAgentAsync(TimeSpan? timeout = null)
    {
        _ = Services;
        await WaitForStateAsync(RCloneServiceState.ServiceState.Running, timeout);
    }

    /// <summary>The hosted <see cref="RCloneService"/> instance.</summary>
    public RCloneService Service =>
        Services.GetServices<IHostedService>().OfType<RCloneService>().Single();

    /// <summary>The state the service is in right now.</summary>
    public RCloneServiceState.ServiceState CurrentState => Service._state.State;

    /// <summary>
    /// Waits for the service to reach a state. A ceiling on a condition, not a
    /// sleep: it returns as soon as the state is reached.
    /// </summary>
    public async Task WaitForStateAsync(
        RCloneServiceState.ServiceState state,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        var service = Service;

        while (DateTime.UtcNow < deadline)
        {
            if (service._state.State == state)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException(
            $"Agent did not reach {state} within the timeout; it is in {service._state.State}.");
    }


    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        /*
         * No logging provider is added here on purpose. BackerAgent/Program.cs:59
         * calls builder.Host.UseSerilog(), whose parameterless form replaces the
         * logger factory outright, so any provider a test adds receives nothing.
         * Assertions therefore use state and recorded stub requests, never logs.
         */

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHannibalServiceClient>();
            services.AddSingleton(Hannibal);

            services.RemoveAll<ConfigHelper<RCloneServiceOptions>>();
            services.AddSingleton(_ =>
            {
                Directory.CreateDirectory(_configDirectory);
                return new ConfigHelper<RCloneServiceOptions>(
                    NullLogger<ConfigHelper<RCloneServiceOptions>>.Instance,
                    b => b,
                    "BackerTests",
                    _configDirectory);
            });

            /*
             * Drop the hosted service that opens SignalR connections, but keep
             * the dictionary itself - RCloneService's constructor indexes into
             * it. The connection is never started.
             */
            services.RemoveAll<Dictionary<string, HubConnection>>();
            services.AddSingleton(new Dictionary<string, HubConnection>
            {
                ["hannibal"] = new HubConnectionBuilder()
                    .WithUrl("http://127.0.0.1:1/hannibal")
                    .Build()
            });

            foreach (var descriptor in services
                         .Where(d => d.ServiceType == typeof(IHostedService)
                                     && d.ImplementationType == typeof(HubConnectionService))
                         .ToList())
            {
                services.Remove(descriptor);
            }

            /*
             * PostConfigure runs after every Configure, so these win over the
             * configuration binding regardless of the order in which
             * BackerAgent/Program.cs assembled its sources.
             */
            services.PostConfigure<RCloneServiceOptions>(options =>
            {
                options.BackerUsername = "agent-test-user";
                options.BackerPassword = "not-a-real-password";

                /*
                 * _checkConfig (RCloneService.cs:1762-1771) refuses to start
                 * unless all of these are non-empty. RClonePath is deliberately
                 * a path that does not exist: with SkipProcessStart honoured,
                 * nothing may try to run it.
                 */
                options.RClonePath = "no-such-rclone-binary";
                options.RCloneOptions = "rcd";
                options.UrlSignalR = "http://127.0.0.1:1";

                options.RCloneUrl = Stub.BaseAddress;
                options.SkipProcessStart = true;
                options.Autostart = true;

                /*
                 * Without this the agent rewrites the real backer-rclone.conf
                 * of the machine running the tests: it is loaded and saved on
                 * every StartAsync (RCloneService.cs:2196-2199) and again after
                 * every backend login. Two hosts in parallel also raced on that
                 * one file.
                 */
                options.ConfigDirectory = RCloneConfigDirectory;

                _configureOptions?.Invoke(options);
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        Stub.DisposeAsync().AsTask().GetAwaiter().GetResult();

        try
        {
            if (Directory.Exists(_configDirectory))
            {
                Directory.Delete(_configDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}
