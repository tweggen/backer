using System.Net;
using System.Net.Http.Headers;
using Poe.Services;
using Tools;

namespace Tools;

public class AddTokenHandler : DelegatingHandler
{
    private readonly ITokenProvider _tokenProvider;
    private readonly AuthState _authState;

    public AddTokenHandler(ITokenProvider tokenProvider, AuthState authState)
    {
        _tokenProvider = tokenProvider;
        _authState = authState;
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
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _authState.ShouldRedirectToLogin = true;
        }

        return response;
    }
}