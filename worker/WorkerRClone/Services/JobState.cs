using Hannibal.Models;
using OAuth2.Client;
using WorkerRClone.Client;

namespace WorkerRClone.Services;

public class JobState
{
    public required Job Job;
    public required HttpClient HttpClient;
    public required RCloneClient RCloneClient;

    public WorkerRClone.Models.Result? Result;
    public EndpointState? SourceEndpointState;
    public EndpointState? DestinationEndpointState;
    
}