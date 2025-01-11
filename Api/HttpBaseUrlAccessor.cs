namespace Api;

public class HttpBaseUrlAccessor : IHttpBaseUrlAccessor
{
    public HttpBaseUrlAccessor(IConfiguration configuration)
    {
        _siteUrlString = configuration["profiles:applicationUrl"];
    }
    private readonly string? _siteUrlString;

    public string? GetHttpsUrl()
    {
        var urls = _siteUrlString.Split(";");

        return urls.FirstOrDefault(g => g.StartsWith("https://"));
    }

    public string? GetHttpUrl()
    {
        var urls = _siteUrlString.Split(";");

        return urls.FirstOrDefault(g => g.StartsWith("http://"));
    }
}