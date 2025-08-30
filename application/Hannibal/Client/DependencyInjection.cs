using System.Net.Http.Headers;
using Hannibal.Client.Configuration;
using Hannibal.Services;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tools;

namespace Hannibal.Client;

public static class DependencyInjection
{
    public static IServiceCollection AddFrontendHannibalServiceClient(
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
            })
            #if false
            .ConfigureHttpClient((sp, client) => {
                var tokenProvider = sp.GetRequiredService<ITokenProvider>();
                var token = tokenProvider.GetToken();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            })
            #endif
            //.AddHttpMessageHandler<IdentityCookieHandler>()
            ;

        return services;
    }


    
    
    public static IServiceCollection AddIdentityApiClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        /*
         * Apply the hannibal client options.
         */
        services.Configure<IdentityApiServiceOptions>(configuration.GetSection("IdentityApiServiceClient"));
        
        
        // Combine the HTTP client registration with the service registration
        services.AddHttpClient<IIdentityApiService, IdentityApiService>((serviceProvider, client) =>
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