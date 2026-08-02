using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tools;

public class AddTokenHandler : DelegatingHandler
{
    private readonly ITokenProvider _tokenProvider;
    private readonly ILogger<AddTokenHandler> _logger;

    public AddTokenHandler(ITokenProvider tokenProvider, ILogger<AddTokenHandler>? logger = null)
    {
        _tokenProvider = tokenProvider;
        _logger = logger ?? NullLogger<AddTokenHandler>.Instance;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await _tokenProvider.GetToken();
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

        return response;
    }
}
