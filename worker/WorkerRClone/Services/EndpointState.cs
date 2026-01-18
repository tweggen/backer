using OAuth2.Client;
using WorkerRClone.Client;

namespace WorkerRClone.Services;

public class EndpointState
{
    public required Hannibal.Models.Endpoint Endpoint;
    public required HttpClient HttpClient;
    public required RCloneClient RCloneClient;
    
    public required string Uri;

    public OAuth2Client? OAuthClient;
}