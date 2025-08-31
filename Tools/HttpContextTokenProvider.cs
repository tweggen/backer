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
            SameSite = SameSiteMode.Strict
        });   
    }
    
    
    public async Task<string?> GetToken()
    {
        return _httpContextAccessor.HttpContext?.Request.Cookies["access_token"];
    }
}