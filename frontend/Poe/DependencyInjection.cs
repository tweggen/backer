using System.Net;
using Hannibal.Client;
using Hannibal.Client.Configuration;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using Poe.Services;
using Tools;

namespace Poe;


internal class FrontendHttpRedirectHandler : DelegatingHandler
{
    private readonly AuthState _authState;

    public FrontendHttpRedirectHandler(AuthState authState)
    {
        _authState = authState;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _authState.ShouldRedirectToLogin = true;
        }

        return response;
    }
}  
    
    
public static class DependencyInjection
{
    public static IServiceCollection AddFrontendHannibalServiceClient(
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
            .AddHttpMessageHandler<AddTokenHandler>()
            ;

        return services;
    }
}