namespace Hannibal.Models;

public class Technologies
{
    private static List<string> _listTechnologies = new()
    {
        "onedrive",
        "dropbox",
        "smb",
        "local"
    };
    
    public static IReadOnlyList<string> GetTechnologies()
    {
        return _listTechnologies.AsReadOnly();
    }
}