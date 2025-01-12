using Hannibal.Client.Configuration;
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
        
        /*
         * Create our http client using the base url.
         */
        services.AddHttpClient<HannibalServiceClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<HannibalServiceClientOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
        });
        
        /*
         * And create our single singleton client object.
         */
        services.AddSingleton<IHannibalServiceClient, HannibalServiceClient>();

        return services;
    }
}