namespace Tools;

public interface IStaticTokenProvider : ITokenProvider
{
    public void SetToken(string token);
}