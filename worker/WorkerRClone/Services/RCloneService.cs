using Hannibal.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Result = WorkerRClone.Models.Result;

namespace WorkerRClone;

public class RCloneService : IRCloneService
{
    private HubConnection _hannibalConnection;

    public RCloneService(Dictionary<string, HubConnection> connections)
    {
        _hannibalConnection = connections["hannibal"];

        _hannibalConnection.On<Job>("NewJobAvailable", (message) =>
        {
            Console.WriteLine($"Received message: {message}");
        });
    }

    public async Task<Result> GetStatus(int jobId, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public async Task<Job> GetCurrentJob(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}