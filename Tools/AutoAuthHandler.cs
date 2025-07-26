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
    private readonly Func<ServiceProvider, CancellationToken, Task<string>> _getAuthCookieAsync;
    private readonly ServiceProvider _serviceProvider;

    public AutoAuthHandler(ServiceProvider serviceProvider, HttpClient authClient, Func<ServiceProvider, CancellationToken, Task<string>> getAuthCookieAsync)
    {
        _serviceProvider = serviceProvider;
        _getAuthCookieAsync = getAuthCookieAsync;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var token = await _getAuthCookieAsync(_serviceProvider, cancellationToken); // Get token with credentials
            System.Console.WriteLine("Got auth cookie: {token}", token);
            
            /*
             * Clone request and retry
             */
            var newRequest = await CloneHttpRequestMessageAsync(request);
            newRequest.Headers.Add("Cookie", $".AspNetCore.Identity.Application={token}");

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