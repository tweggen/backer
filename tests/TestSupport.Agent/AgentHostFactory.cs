using Hannibal.Client;
using Hannibal.Models;
using Microsoft.Extensions.Configuration;
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

namespace TestSupport.Agent;

/// <summary>
/// Where the hosted agent gets its Hannibal API from.
/// </summary>
public enum HannibalClientMode
{
    /// <summary>
    /// An NSubstitute double, configured through
    /// <see cref="AgentHostFactory.Hannibal"/>. The default: the agent-only
    /// suite has no API to talk to.
    /// </summary>
    Substitute,

    /// <summary>
    /// The production client registration is left in place, so the agent
    /// authenticates and calls for real. The caller is responsible for
    /// pointing its transport somewhere, via
    /// <see cref="AgentHostOptions.ConfigureServices"/>.
    /// </summary>
    Real
}

/// <summary>
/// How to build one agent host.
/// </summary>
public sealed class AgentHostOptions
{
    public HannibalClientMode HannibalClient { get; set; } = HannibalClientMode.Substitute;

    /// <summary>
    /// Applied after the harness defaults, so a test can deviate from them -
    /// for instance to point the agent at a port nothing listens on.
    /// </summary>
    public Action<RCloneServiceOptions>? ConfigureRCloneOptions { get; set; }

    /// <summary>
    /// Applied after the harness's own service overrides.
    /// </summary>
    public Action<IServiceCollection>? ConfigureServices { get; set; }

    /// <summary>
    /// Builds the agent's connection to the Hannibal hub.
    ///
    /// <para>Left null - the default - the agent gets a connection object that
    /// is never started, and <c>HubConnectionService</c> is dropped so nothing
    /// dials out. <c>RCloneService</c> still resolves
    /// <c>connections["hannibal"]</c> in its constructor
    /// (<c>RCloneService.cs:145</c>) and only ever subscribes to it.</para>
    ///
    /// <para>Set it, and the connection is started for real. That matters for
    /// more than notifications: without it the agent only notices new work on
    /// its 120-second safety-net poll
    /// (<c>_jobPollInterval</c>, <c>RCloneService.cs:64</c>).</para>
    /// </summary>
    public Func<HubConnection>? HannibalHubConnection { get; set; }
}

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

    private readonly AgentHostOptions _options;

    private AgentHostFactory(RCloneStub stub, AgentHostOptions options)
    {
        Stub = stub;
        _options = options;
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
    public static Task<AgentHostFactory> CreateAsync(
        Action<RCloneServiceOptions>? configureRCloneOptions)
        => CreateAsync(new AgentHostOptions { ConfigureRCloneOptions = configureRCloneOptions });

    public static async Task<AgentHostFactory> CreateAsync(AgentHostOptions? options = null)
    {
        options ??= new AgentHostOptions();

        var stub = await RCloneStub.StartAsync();
        var factory = new AgentHostFactory(stub, options);

        if (options.HannibalClient == HannibalClientMode.Substitute)
        {
            /*
             * A user must exist or _checkOnlineImpl treats it as an
             * authentication failure and parks in WaitConfig
             * (RCloneService.cs:1104-1116).
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
        }

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
    /// The agent's connection to the Hannibal hub - the one
    /// <c>RCloneService</c> subscribes to for <c>NewJobAvailable</c>.
    /// </summary>
    public HubConnection HannibalConnection =>
        Services.GetRequiredService<Dictionary<string, HubConnection>>()["hannibal"];

    /// <summary>
    /// Waits for the hub connection to reach <paramref name="state"/>.
    /// </summary>
    public async Task WaitForHubStateAsync(
        HubConnectionState state,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));

        while (DateTime.UtcNow < deadline)
        {
            if (HannibalConnection.State == state)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException(
            $"The hub connection did not reach {state}; it is {HannibalConnection.State}.");
    }

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

        var effective = _effectiveOptions();

        /*
         * The same values have to reach raw configuration as well, not only the
         * options pipeline: BackerAgent/DependencyInjection.cs:44-45 rebinds
         * IConfiguration into a fresh RCloneServiceOptions to find the
         * credentials it logs in with, so a PostConfigure alone leaves that
         * path authenticating as nobody. Program.cs:184 reads UrlSignalR the
         * same way.
         */
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RCloneService:BackerUsername"] = effective.BackerUsername,
                ["RCloneService:BackerPassword"] = effective.BackerPassword,
                ["RCloneService:UrlSignalR"] = effective.UrlSignalR,
                ["RCloneService:RClonePath"] = effective.RClonePath,
                ["RCloneService:RCloneOptions"] = effective.RCloneOptions,
                ["RCloneService:RCloneUrl"] = effective.RCloneUrl,
                ["RCloneService:ConfigDirectory"] = effective.ConfigDirectory,
                ["RCloneService:SkipProcessStart"] = effective.SkipProcessStart ? "true" : "false",
                ["RCloneService:SkipJobAcquisition"] = effective.SkipJobAcquisition ? "true" : "false",
                ["RCloneService:Autostart"] = effective.Autostart ? "true" : "false"
            });
        });

        builder.ConfigureServices(services =>
        {
            if (_options.HannibalClient == HannibalClientMode.Substitute)
            {
                services.RemoveAll<IHannibalServiceClient>();
                services.AddSingleton(Hannibal);
            }

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

            services.RemoveAll<Dictionary<string, HubConnection>>();
            services.AddSingleton(new Dictionary<string, HubConnection>
            {
                ["hannibal"] = _options.HannibalHubConnection?.Invoke()
                               ?? new HubConnectionBuilder()
                                   .WithUrl("http://127.0.0.1:1/hannibal")
                                   .Build()
            });

            if (_options.HannibalHubConnection is null)
            {
                /*
                 * Nothing to connect to, so drop the hosted service that would
                 * dial out and retry forever.
                 */
                foreach (var descriptor in services
                             .Where(d => d.ServiceType == typeof(IHostedService)
                                         && d.ImplementationType == typeof(HubConnectionService))
                             .ToList())
                {
                    services.Remove(descriptor);
                }
            }

            /*
             * PostConfigure runs after every Configure, so this wins over the
             * configuration binding regardless of the order in which
             * BackerAgent/Program.cs assembled its sources.
             */
            services.PostConfigure<RCloneServiceOptions>(options => _apply(effective, options));

            _options.ConfigureServices?.Invoke(services);
        });
    }


    /// <summary>
    /// The options this agent should run with: harness defaults, then whatever
    /// the test asked for.
    /// </summary>
    private RCloneServiceOptions _effectiveOptions()
    {
        var options = new RCloneServiceOptions
        {
            BackerUsername = "agent-test-user",
            BackerPassword = "not-a-real-password",

            /*
             * _checkConfig (RCloneService.cs:1762-1771) refuses to start unless
             * all of these are non-empty. RClonePath is deliberately a path
             * that does not exist: with SkipProcessStart honoured, nothing may
             * try to run it.
             */
            RClonePath = "no-such-rclone-binary",
            RCloneOptions = "rcd",
            UrlSignalR = "http://127.0.0.1:1",

            RCloneUrl = Stub.BaseAddress,
            SkipProcessStart = true,
            Autostart = true,

            /*
             * Without this the agent rewrites the real backer-rclone.conf of
             * the machine running the tests: it is loaded and saved on every
             * StartAsync (RCloneService.cs:2196-2199) and again after every
             * backend login. Two hosts in parallel also raced on that one file.
             */
            ConfigDirectory = RCloneConfigDirectory
        };

        _options.ConfigureRCloneOptions?.Invoke(options);
        return options;
    }


    private static void _apply(RCloneServiceOptions source, RCloneServiceOptions target)
    {
        target.BackerUsername = source.BackerUsername;
        target.BackerPassword = source.BackerPassword;
        target.RClonePath = source.RClonePath;
        target.RCloneOptions = source.RCloneOptions;
        target.RCloneUrl = source.RCloneUrl;
        target.SkipProcessStart = source.SkipProcessStart;
        target.SkipJobAcquisition = source.SkipJobAcquisition;
        target.ConfigDirectory = source.ConfigDirectory;
        target.UrlSignalR = source.UrlSignalR;
        target.Autostart = source.Autostart;
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
