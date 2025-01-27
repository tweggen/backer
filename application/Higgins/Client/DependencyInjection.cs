using Higgins.Client.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Higgins.Client;

public static class DependencyInjection
{
    public static IServiceCollection AddHigginsServiceClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        /*
         * Apply the Higgins client options.
         */
        services.Configure<HigginsServiceClientOptions>(configuration.GetSection("HigginsServiceClient"));
        
        
        // Combine the HTTP client registration with the service registration
        services.AddHttpClient<IHigginsServiceClient, HigginsServiceClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<HigginsServiceClientOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
        });

        return services;
    }

}