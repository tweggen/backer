using System.Net;
using Hannibal;
using Hannibal.Client;
using Hannibal.Client.Configuration;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Tools;
using WorkerRClone.Configuration;

namespace BackerAgent;

public static class DependencyInjection
{

    public static IServiceCollection AddBackgroundHannibalServiceClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        /*
         * Apply the hannibal client options.
         */
        services
            .Configure<HannibalServiceClientOptions>(configuration.GetSection("HannibalServiceClient"));
        
        
        // Combine the HTTP client registration with the service registration
        services
            .AddHttpClient<IHannibalServiceClient, HannibalServiceClient>((serviceProvider, client) =>
            {
                var options = serviceProvider.GetRequiredService<IOptions<HannibalServiceClientOptions>>().Value;
                client.BaseAddress = new Uri(options.BaseUrl);
            })
            .AddHttpMessageHandler(sp0 =>
            {
                var authClient = new HttpClient();
                return new Tools.AutoAuthHandler(
                    sp0,
                    sp0.GetRequiredService<IStaticTokenProvider>(),
                    authClient,
                    async (sp, cancellationToken) =>
                    {
                        /*
                         * We are supposed to return the authenticated token.
                         */
                        var apiOptions = new RCloneServiceOptions();
                        configuration.GetSection("RCloneService").Bind(apiOptions);
                        using var scope = sp.GetRequiredService<IServiceScopeFactory>().CreateScope();
                        IIdentityApiService identityApiService =
                            scope.ServiceProvider.GetRequiredService<IIdentityApiService>();
                        var loginRes = await identityApiService.TokenAsync(new()
                                { Email = apiOptions.BackerUsername, Password = apiOptions.BackerPassword },
                            cancellationToken);

                        if (loginRes.Result is Ok<AccessTokenResponse> okResult)
                        {
                            return okResult.Value!.AccessToken;
                        }
                        else
                        {
                            return "";
                        }
                    });
            });
            ;

        return services;
        #if false
        var cookieContainer = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = cookieContainer,
            UseCookies = true
        };
        
        services
            .AddHttpClient<IHannibalServiceClient, HannibalServiceClient>((serviceProvider, client) =>
            {
                var options = serviceProvider.GetRequiredService<IOptions<HannibalServiceClientOptions>>().Value;
                client.BaseAddress = new Uri(options.BaseUrl);
            })
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                return new HttpClientHandler
                {
                    UseCookies = true,
                    CookieContainer = cookieContainer,
                    AllowAutoRedirect = true // tweak as needed
                };
            })
            .AddHttpMessageHandler(sp =>
            {
                var authClient = new HttpClient();
                return new Tools.AutoAuthHandler(
                    services.BuildServiceProvider(),
                    cookieContainer,
                    authClient,
                    async (sp, cancellationToken) =>
                    {
                        /*
                         * We are supposed to return the authenticated token.
                         */
                        var apiOptions = new RCloneServiceOptions();
                        configuration.GetSection("RCloneService").Bind(apiOptions);
                        using var scope = sp.GetRequiredService<IServiceScopeFactory>().CreateScope();
                        IIdentityApiService identityApiService =
                            scope.ServiceProvider.GetRequiredService<IIdentityApiService>();
                        var loginRes = await identityApiService.LoginUserAsync(new()
                                { Email = apiOptions.BackerUsername, Password = apiOptions.BackerPassword },
                            cancellationToken);

                        if (loginRes.Result is Ok<AccessTokenResponse> okResult)
                        {
                            return okResult.Value!.AccessToken;
                        }
                        else
                        {
                            return "";
                        }
                    });
            });
        #endif


        return services;
    }
}
