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
    private readonly CookieContainer _cookieContainer;

    public static void AddSetCookieToContainer(
        CookieContainer container, 
        Uri uri, 
        string setCookie,
        HttpRequestMessage? request = null)
    {
        string[] parts = setCookie.Split(';');
        string[] nameValue = parts[0].Split('=', 2);

        var cookie = new Cookie(nameValue[0].Trim(), nameValue[1].Trim());

        foreach (var part in parts[1..])
        {
            var trimmed = part.Trim().ToLowerInvariant();
            if (trimmed.StartsWith("path=")) cookie.Path = part.Substring(5).Trim();
            else if (trimmed == "secure") cookie.Secure = true;
            else if (trimmed == "httponly") cookie.HttpOnly = true;
            else if (trimmed.StartsWith("domain=")) cookie.Domain = part.Substring(7).Trim();
            // You could add more logic for SameSite if needed
        }

        request?.Headers.Add("Cookie", cookie.ToString());
        container.Add(uri, cookie);
    }
    
    public AutoAuthHandler(
        ServiceProvider serviceProvider, 
        CookieContainer cookieContainer,
        HttpClient authClient, 
        Func<ServiceProvider, 
            CancellationToken, 
            Task<string>> getAuthCookieAsync)
    {
        _serviceProvider = serviceProvider;
        _getAuthCookieAsync = getAuthCookieAsync;
        _cookieContainer = cookieContainer;
    }

    
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var setCookieValue = await _getAuthCookieAsync(_serviceProvider, cancellationToken); // Get token with credentials
            System.Console.WriteLine($"Got set cookie value: {setCookieValue}");
            
            var originalUri = request.RequestUri;

            // Build domain-level Uri (scheme + host + optional port)
            var domainUri = new Uri($"{originalUri!.Scheme}://{originalUri.Host}" +
                                    (originalUri.IsDefaultPort ? "" : $":{originalUri.Port}"));
            
            
            /*
             * Clone request and retry
             */
            var newRequest = await CloneHttpRequestMessageAsync(request);

            AddSetCookieToContainer(_cookieContainer, domainUri, setCookieValue, newRequest);

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