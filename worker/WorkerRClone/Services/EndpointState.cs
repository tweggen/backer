using OAuth2.Client;
using WorkerRClone.Client;

namespace WorkerRClone.Services;

public class EndpointState
{
    // public ProviderState ProviderState;
    public required Hannibal.Models.Endpoint Endpoint;
    public required string Uri;
}