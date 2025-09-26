namespace Tools;

public class AppBasePath
{
    public string Value { get; }
    public AppBasePath(string value) => Value = value.TrimEnd('/');
}