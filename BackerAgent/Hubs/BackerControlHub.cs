using Microsoft.AspNetCore.SignalR;
using WorkerGit.Services;
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
    private readonly GitWorkerService _gitWorkerService;
    private readonly ILogger<BackerControlHub> _logger;

    public BackerControlHub(
        RCloneService rcloneService,
        GitWorkerService gitWorkerService,
        ILogger<BackerControlHub> logger)
    {
        _rcloneService = rcloneService;
        _gitWorkerService = gitWorkerService;
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
    /// Client can request current job transfer stats at any time
    /// </summary>
    public async Task RequestJobTransferStats()
    {
        try
        {
            var stats = await _rcloneService.GetJobTransferStatsAsync(CancellationToken.None);
            await Clients.Caller.SendAsync("JobTransferStatsUpdated", stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting job transfer stats");
        }
    }

    /// <summary>
    /// Client can abort a specific job by its Hannibal job ID
    /// </summary>
    public async Task AbortJob(int hannibalJobId)
    {
        try
        {
            /*
             * Route by ownership rather than a guessed engine (same
             * reasoning as the /jobs/{jobId}/abort minimal API endpoint in
             * Program.cs): neither call has a side effect on a job it does
             * not own, so trying both in a fixed order cannot misfire.
             * Git first because TryAbortJobAsync is a cheap, in-process
             * dictionary lookup and CTS cancellation - no network round trip
             * to rclone's RC interface the way RCloneService.AbortJobAsync
             * makes.
             */
            bool ownedByGit = await _gitWorkerService.TryAbortJobAsync(hannibalJobId);
            if (!ownedByGit)
            {
                await _rcloneService.AbortJobAsync(hannibalJobId, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error aborting job {JobId}", hannibalJobId);
        }
    }
}
