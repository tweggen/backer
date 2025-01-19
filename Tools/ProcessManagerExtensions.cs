using Microsoft.Extensions.DependencyInjection;

namespace Tools;

public static class ProcessManagerExtensions
{
    public static IServiceCollection AddProcessManager(this IServiceCollection services)
    {
        return services.AddSingleton<ProcessManager>();
    }
}

