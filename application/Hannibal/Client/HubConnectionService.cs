using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;

namespace Hannibal.Client;

public class HubConnectionService : BackgroundService
{
    private readonly Dictionary<string, HubConnection> _connections;

    public HubConnectionService(Dictionary<string, HubConnection> connections)
    {
        _connections = connections;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var conn in _connections.Values)
        {
            await StartWithRetry(conn, stoppingToken);
        }
    }

    private async Task StartWithRetry(HubConnection conn, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await conn.StartAsync(token);
                Console.WriteLine("Connection started.");
                return;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error starting connection: {e.Message}");
                await Task.Delay(2000, token);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var conn in _connections.Values)
        {
            await conn.StopAsync(cancellationToken);
        }
    }
}