using System.Text.Json;
using System.Text.Json.Serialization;
using Hannibal.Configuration;
using Hannibal.Models;

namespace WorkerRClone.Configuration;


public class RCloneServiceOptions
{
    public string? BackerUsername { get; set; }
    
    public string? BackerPassword { get; set; }
    
    /**
     * Where can we find the rclone executable?
     */
    public string? RClonePath { get; set; }
    
    public string? RCloneOptions { get; set; }
    
    public string? UrlSignalR { get; set; }

    /**
     * Shall the rclone operations be started automatically on startup?
     */
    public bool Autostart { get; set; }
    
    [JsonPropertyName("oauth2")]
    public OAuthOptions? OAuth2 { get; set; }
    
    
    public override string ToString()
    {
        return JsonSerializer.Serialize(this);
    }

    public RCloneServiceOptions(RCloneServiceOptions o)
    {
        BackerUsername = o.BackerUsername;
        BackerPassword = o.BackerPassword;
        RClonePath = o.RClonePath;
        RCloneOptions = o.RCloneOptions;
        UrlSignalR = o.UrlSignalR;
        Autostart = o.Autostart;
        OAuth2 = OAuth2 != null ? new OAuthOptions(o.OAuth2) : null;
    }

    public RCloneServiceOptions()
    {
    }
}