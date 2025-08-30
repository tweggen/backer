namespace Tools;

public interface ITokenProvider
{
    public Task<string?> GetToken();
}