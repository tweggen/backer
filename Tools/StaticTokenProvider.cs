namespace Tools;

public class StaticTokenProvider : IStaticTokenProvider
{
    private string? _token = null;


    public void SetToken(string token)
    {
        _token = token;
    }
    
    
    public async Task<string?> GetToken()
    {
        return _token;
    }
}