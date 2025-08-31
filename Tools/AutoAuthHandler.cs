using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Tools;

public class AutoAuthHandler : DelegatingHandler
{
    private readonly HttpClient _authClient;
    private readonly string _authEndpoint;
    private readonly string _username;
    private readonly string _password;
    private readonly Func<IServiceProvider, CancellationToken, Task<string>> _obtainTokenAsync;
    private readonly IServiceProvider _serviceProvider;
    private readonly IStaticTokenProvider _staticTokenProvider;

    
    public AutoAuthHandler(
        IServiceProvider serviceProvider, 
        IStaticTokenProvider staticTokenProvider,
        HttpClient authClient, 
        Func<IServiceProvider, 
            CancellationToken, 
            Task<string>> obtainTokenAsync)
    {
        _serviceProvider = serviceProvider;
        _obtainTokenAsync = obtainTokenAsync;
        _staticTokenProvider = staticTokenProvider;
    }

    
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _staticTokenProvider.GetToken();
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var newToken = await _obtainTokenAsync(_serviceProvider, cancellationToken); // Get token with credentials
            _staticTokenProvider.SetToken(newToken);
            
            System.Console.WriteLine($"Set new jwt token: {newToken}");
            
            var originalUri = request.RequestUri;

            // Build domain-level Uri (scheme + host + optional port)
            var domainUri = new Uri($"{originalUri!.Scheme}://{originalUri.Host}" +
                                    (originalUri.IsDefaultPort ? "" : $":{originalUri.Port}"));
            
            
            /*
             * Clone request and retry
             */
            var newRequest = await CloneHttpRequestMessageAsync(request);


            return await base.SendAsync(newRequest, cancellationToken);
        }

        return response;
    }

    
    private async Task<HttpRequestMessage> CloneHttpRequestMessageAsync(HttpRequestMessage request)
    {
        var newRequest = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Content = request.Content,
            Version = request.Version
        };

        foreach (var header in request.Headers)
            newRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);

        return newRequest;
    }
}