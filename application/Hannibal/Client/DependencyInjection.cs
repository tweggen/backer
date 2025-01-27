using Hannibal.Client.Configuration;
using Hannibal.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Hannibal.Client;

public static class DependencyInjection
{
    public static IServiceCollection AddHannibalServiceClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        /*
         * Apply the hannibal client options.
         */
        services.Configure<HannibalServiceClientOptions>(configuration.GetSection("HannibalServiceClient"));
        
        
        // Combine the HTTP client registration with the service registration
        services.AddHttpClient<IHannibalServiceClient, HannibalServiceClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<HannibalServiceClientOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
        });

        return services;
    }
    
    public static IServiceCollection AddHannibalBackofficeService(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddHostedService<BackofficeService>();
        
        return services;
    }

}