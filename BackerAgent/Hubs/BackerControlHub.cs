using Microsoft.AspNetCore.SignalR;
using WorkerRClone.Models;
using WorkerRClone.Services;

namespace BackerAgent.Hubs;

/// <summary>
/// SignalR Hub for BackerControl (local desktop control panel) communication.
/// This is BackerAgent's SERVER role - BackerControl connects as a client.
/// Distinct from BackerAgent's CLIENT role connecting to Hannibal.
/// </summary>
public class BackerControlHub : Hub
{
    private readonly RCloneService _rcloneService;
    private readonly ILogger<BackerControlHub> _logger;

    public BackerControlHub(
        RCloneService rcloneService,
        ILogger<BackerControlHub> logger)
    {
        _rcloneService = rcloneService;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("BackerControl client connected: {ConnectionId}", Context.ConnectionId);
        
        // Send current state immediately when client connects
        var currentState = _rcloneService.GetState();
        await Clients.Caller.SendAsync("ServiceStateChanged", currentState);
        
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("BackerControl client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Client can request current state at any time
    /// </summary>
    public async Task RequestCurrentState()
    {
        var currentState = _rcloneService.GetState();
        await Clients.Caller.SendAsync("ServiceStateChanged", currentState);
    }

    /// <summary>
    /// Client can request current transfer stats at any time
    /// </summary>
    public async Task RequestTransferStats()
    {
        try
        {
            var stats = await _rcloneService.GetTransferStatsAsync(CancellationToken.None);
            await Clients.Caller.SendAsync("TransferStatsUpdated", stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting transfer stats");
        }
    }
}
