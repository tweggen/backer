using Microsoft.AspNetCore.Http;

namespace Tools;

public class ConstantTokenProvider : IStaticTokenProvider
{
    private string _token = null;

    public ConstantTokenProvider(IHttpContextAccessor httpContextAccessor)
    {
        _token = null;
    }
    
    public void SetToken(string token)
    {
        _token = token;
    }
    
    
    public async Task<string?> GetToken()
    {
        return _token;
    }
}