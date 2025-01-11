using System.ComponentModel;
using Hannibal.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Result = WorkerRClone.Models.Result;

namespace WorkerRClone;

public class RCloneService : BackgroundService
{
    private HubConnection _hannibalConnection;
    private ILogger<RCloneService> _logger;

    public RCloneService(ILogger<RCloneService> logger, Dictionary<string, HubConnection> connections)
    {
        _logger = logger;
        _hannibalConnection = connections["hannibal"];

        _hannibalConnection.On<Job>("NewJobAvailable", (message) =>
        {
            Console.WriteLine($"Received message: {message}");
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
            await Task.Delay(1_000, stoppingToken);
        }
    }
}