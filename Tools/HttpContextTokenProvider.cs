using Microsoft.AspNetCore.Http;
using Tools;

public class HttpContextTokenProvider : IStaticTokenProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextTokenProvider(IHttpContextAccessor httpContextAccessor)
    {
        //var trace = Environment.StackTrace;
        //Console.WriteLine($"[TokenStore] Constructed at:\n{trace}");
        _httpContextAccessor = httpContextAccessor;
    }
    
    public void SetToken(string token)
    {
        _httpContextAccessor.HttpContext!.Response.Cookies.Append("access_token", token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            // Lax (not Strict) so the cookie is still sent on the top-level
            // navigation back from an external OAuth2 provider (cross-site GET).
            SameSite = SameSiteMode.Lax
        });
    }
    
    
    public async Task<string?> GetToken()
    {
        return _httpContextAccessor.HttpContext?.Request.Cookies["access_token"];
    }
}