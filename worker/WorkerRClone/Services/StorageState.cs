using Hannibal.Models;
using OAuth2.Client;
using WorkerRClone.Client;

namespace WorkerRClone.Services;

public class StorageState
{
    public required Storage Storage;
    
    /**
     * Parameters for rclone configuration. 
     */
    public SortedDictionary<string, string> RCloneParameters = new();
    
    /**
     * OAuth client, if required
     */
    public OAuth2Client? OAuthClient;

    /**
     * http client for communication with the actual service,
     * if required.
     */
    public HttpClient? HttpClient;
}