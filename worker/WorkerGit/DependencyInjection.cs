using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Tools;
using WorkerGit.Configuration;
using WorkerGit.Services;

namespace WorkerGit;

public static class DependencyInjection
{
    /**
     * Registers the git mirror engine's hosted slice (plan-git-repo-storage.md
     * Design §2), sibling to <c>WorkerRClone.AddRCloneService</c>.
     */
    public static IServiceCollection AddGitWorker(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<GitWorkerOptions>(configuration.GetSection("GitWorker"));

        // GitCliRunner/GitCacheManager/GitMirrorEngine (slice 1) all take a
        // plain GitWorkerOptions, not an IOptionsMonitor - resolve it once
        // here, filling in the default cache root a test always overrides.
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<GitWorkerOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.CacheRoot))
            {
                options.CacheRoot = Path.Combine(EnvironmentDetector.GetConfigDir("Backer"), "git-cache");
            }

            /*
             * RCloneService:SkipJobAcquisition is the documented "this agent
             * takes no work" smoke-test switch (CLAUDE.md). Honour it
             * agent-wide instead of requiring the operator to know a second
             * flag exists; GitWorker:SkipJobAcquisition alone still works.
             */
            if (configuration.GetValue<bool>("RCloneService:SkipJobAcquisition"))
            {
                options.SkipJobAcquisition = true;
            }

            return options;
        });

        // Null by default - a production agent's command history is
        // unbounded over its lifetime, unlike a test's RecordingGitCommandTrace
        // (WorkerGit.Tests, and any hosted test that wants to inspect it,
        // overrides this registration before the host is built).
        services.AddSingleton<IGitCommandTrace>(NullGitCommandTrace.Instance);

        services.AddSingleton(sp => new GitCliRunner(
            sp.GetRequiredService<GitWorkerOptions>(),
            sp.GetRequiredService<IGitCommandTrace>()));

        services.AddSingleton<GitCacheManager>();
        services.AddSingleton<GitMirrorEngine>();

        services.AddHostedService<GitWorkerService>();

        // Same trick BackerAgent/Program.cs uses for RCloneService: pull the
        // instance already created for IHostedService back out and register
        // it under its own concrete type too, so BackerControlHub (and any
        // minimal API endpoint) can take a GitWorkerService constructor
        // parameter directly, the way it already does for RCloneService.
        services.AddSingleton(sp => sp.GetServices<IHostedService>().OfType<GitWorkerService>().First());

        return services;
    }
}
