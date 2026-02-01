namespace Hannibal.Models;

public class Technologies
{
    private static List<string> _listTechnologies = new()
    {
        "onedrive",
        "dropbox",
        "googledrive",
        "nextcloud",
        "smb",
        "local"
    };
    
    public static IReadOnlyList<string> GetTechnologies()
    {
        return _listTechnologies.AsReadOnly();
    }
}
