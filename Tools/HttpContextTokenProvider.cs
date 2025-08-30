using Microsoft.AspNetCore.Http;

namespace Tools;

public class HttpContextTokenProvider : ITokenProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextTokenProvider(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<string?> GetToken()
    {
        return _httpContextAccessor.HttpContext?.User?.FindFirst("access_token")?.Value;
    }
}