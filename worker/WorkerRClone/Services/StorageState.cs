using Hannibal.Models;
using OAuth2.Client;
using WorkerRClone.Client;

namespace WorkerRClone.Services;

public class StorageState
{
    public required Storage Storage; 
    public required HttpClient HttpClient;
    public required RCloneClient RCloneClient;
    
    public OAuth2Client? OAuthClient;
}