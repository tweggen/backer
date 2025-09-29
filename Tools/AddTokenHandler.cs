using System.Net;
using System.Net.Http.Headers;
using Poe.Services;
using Tools;

namespace Tools;

public class AddTokenHandler : DelegatingHandler
{
    private readonly ITokenProvider _tokenProvider;

    public AddTokenHandler(ITokenProvider tokenProvider)
    {
        _tokenProvider = tokenProvider;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await _tokenProvider.GetToken();
        Console.WriteLine($"[AddTokenHandler] Token: {token}");
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await base.SendAsync(request, cancellationToken);

        return response;
    }
}