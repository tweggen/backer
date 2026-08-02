using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tools;

public class AutoAuthHandler : DelegatingHandler
{
    private readonly Func<IServiceProvider, CancellationToken, Task<string>> _obtainTokenAsync;
    private readonly IServiceProvider _serviceProvider;
    private readonly IStaticTokenProvider _staticTokenProvider;
    private readonly ILogger<AutoAuthHandler> _logger;


    public AutoAuthHandler(
        IServiceProvider serviceProvider,
        IStaticTokenProvider staticTokenProvider,
        Func<IServiceProvider,
            CancellationToken,
            Task<string>> obtainTokenAsync,
        ILogger<AutoAuthHandler>? logger = null)
    {
        _serviceProvider = serviceProvider;
        _obtainTokenAsync = obtainTokenAsync;
        _staticTokenProvider = staticTokenProvider;
        _logger = logger ?? NullLogger<AutoAuthHandler>.Instance;
    }


    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        /*
         * A 401 makes us re-send the request. The content of the original request
         * can only be serialized to the wire once (anything stream backed is
         * forward-only), so buffer it up front and rebuild it for the retry.
         */
        byte[]? bufferedContent = null;
        if (request.Content is not null)
        {
            await request.Content.LoadIntoBufferAsync();
            bufferedContent = await request.Content.ReadAsByteArrayAsync(cancellationToken);
        }

        var token = await _staticTokenProvider.GetToken();
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            _logger.LogDebug(
                "Attached bearer token to {Method} request, token length {Length}.",
                request.Method, token.Length);
        }
        else
        {
            _logger.LogDebug("No bearer token available for {Method} request.", request.Method);
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var newToken = await _obtainTokenAsync(_serviceProvider, cancellationToken); // Get token with credentials
            if (string.IsNullOrWhiteSpace(newToken))
            {
                /*
                 * If we could not login and acquire a token, we unfortunately need to pass through the auth problem.
                 */
                _logger.LogDebug("Token refresh after 401 yielded no token, passing the 401 through.");
                response.Dispose();
                return new HttpResponseMessage(HttpStatusCode.Unauthorized) { RequestMessage = request };
            }
            _staticTokenProvider.SetToken(newToken);

            _logger.LogDebug("Obtained a new jwt token after 401, token length {Length}.", newToken.Length);

            /*
             * Clone request and retry
             */
            response.Dispose();
            var newRequest = CloneHttpRequestMessage(request, bufferedContent);
            newRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", newToken);

            return await base.SendAsync(newRequest, cancellationToken);
        }

        return response;
    }


    private static HttpRequestMessage CloneHttpRequestMessage(HttpRequestMessage request, byte[]? bufferedContent)
    {
        var newRequest = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };

        if (bufferedContent is not null)
        {
            var newContent = new ByteArrayContent(bufferedContent);
            if (request.Content is not null)
            {
                foreach (var header in request.Content.Headers)
                    newContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            newRequest.Content = newContent;
        }

        foreach (var header in request.Headers)
            newRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);

        foreach (var option in (IDictionary<string, object?>)request.Options)
            ((IDictionary<string, object?>)newRequest.Options)[option.Key] = option.Value;

        return newRequest;
    }
}
