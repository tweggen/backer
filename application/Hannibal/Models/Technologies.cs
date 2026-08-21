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
        "local",
        "git"
    };

    private static readonly HashSet<string> _rcloneTechnologies = new(StringComparer.Ordinal)
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

    /**
     * True for exactly the technologies rclone can move files for. "git" and
     * anything unrecognised are deliberately excluded, not merely omitted:
     * RCloneStorages writes an EMPTY rclone.conf section for any technology
     * its provider factory does not know (it logs a warning and returns empty
     * parameters, see RCloneStorages.cs), so an agent that iterated storages
     * unfiltered would silently leak a non-rclone storage into rclone.conf as
     * a blank remote. This predicate is the filter the agent applies before a
     * storage list ever reaches that code path.
     */
    public static bool IsRCloneTechnology(string? technology)
    {
        return technology is not null && _rcloneTechnologies.Contains(technology);
    }
}
