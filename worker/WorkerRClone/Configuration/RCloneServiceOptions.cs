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

    /**
     * Where rclone's remote control interface listens, e.g.
     * "http://localhost:5572". Leave unset for the well-known default; when
     * set it wins over both the default and whatever a started rclone
     * advertises, which is how tests point the agent at a stub.
     */
    public string? RCloneUrl { get; set; }

    /**
     * Never try to start an rclone process, only talk to one that is already
     * listening. Belt to RCloneUrl's braces for tests: with this set, a
     * misconfigured RClonePath cannot spawn anything.
     */
    public bool SkipProcessStart { get; set; }

    /**
     * Where backer-rclone.conf lives. Leave unset for the machine's Backer
     * config directory, which is what production uses. Tests must set it:
     * without it a hosted agent rewrites the real rclone configuration of
     * whoever runs the suite.
     */
    public string? ConfigDirectory { get; set; }

    /**
     * Log in, connect and start rclone as usual, but never acquire or execute
     * jobs - they are left for the user's other agents. For running an agent
     * that must not interfere with the machines doing the actual work, e.g. a
     * startup smoke test beside a live deployment.
     */
    public bool SkipJobAcquisition { get; set; }

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
        RCloneUrl = o.RCloneUrl;
        SkipProcessStart = o.SkipProcessStart;
        ConfigDirectory = o.ConfigDirectory;
        SkipJobAcquisition = o.SkipJobAcquisition;
        UrlSignalR = o.UrlSignalR;
        Autostart = o.Autostart;
        OAuth2 = o.OAuth2 != null ? new OAuthOptions(o.OAuth2) : null;
    }

    public RCloneServiceOptions()
    {
    }
}