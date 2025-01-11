namespace Api;

public interface IHttpBaseUrlAccessor
{
    string? GetHttpsUrl();
    string? GetHttpUrl();
}