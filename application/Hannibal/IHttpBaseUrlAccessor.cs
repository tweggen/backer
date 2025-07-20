namespace Hannibal;

public interface IHttpBaseUrlAccessor
{
    string? GetHttpsUrl();
    string? GetHttpUrl();
}