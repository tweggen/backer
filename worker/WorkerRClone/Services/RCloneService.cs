using System.ComponentModel;
using Hannibal.Client;
using Hannibal.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Result = WorkerRClone.Models.Result;

namespace WorkerRClone;

public class RCloneService : BackgroundService
{
    private static object _classLock = new();
    private static int _nextId;

    private object _lo = new();
    
    private string _ownerId;
    private int _nRunningJobs = 0;
    
    private HubConnection _hannibalConnection;
    private IHannibalServiceClient _hannibalClient;
    private ILogger<RCloneService> _logger;

    public RCloneService(
        ILogger<RCloneService> logger, 
        Dictionary<string, HubConnection> connections,
        IHannibalServiceClient hannibalClient)
    {
        lock (_classLock)
        {
            _ownerId = $"worker-rclone-{_nextId++}";
        }
        _logger = logger;
        _hannibalConnection = connections["hannibal"];
        _hannibalClient = hannibalClient;

        _hannibalConnection.On<Job>("NewJobAvailable", (message) =>
        {
            Console.WriteLine($"Received message: {message}");
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        /*
         * Initially, we trigger reading all matching todos from hannibal.
         * Whatever we got we execute.
         * If we have nothing, we sleep until receiving an signalr update.
         */
        _triggerFetchJob();
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1_000, stoppingToken);
        }
    }


    private async Task _triggerFetchJob()
    {
        try
        {
            /*
             * Check for the maximum of concurrent jobs.
             */
            lock (_lo)
            {
                // TXWTODO: Read this from config.
                if (_nRunningJobs >= 10)
                {
                    /*
                     * This is too much, I will not execute it now.
                     */
                    return;
                }
            }

            /*
             * Then get the next job.
             */
            var job = await _hannibalClient.AcquireNextJobAsync(
                "rclone", 
                _ownerId);
            if (null == job)
            {
                /*
                 * There is not any job available, go asleep.
                 */
                return;
            }

            /*
             * Execute the job, remember the result.
             */
            Result jobResult = new();
            try
            {
                /*
                 * Execute the job.
                 */
                jobResult.Status = 0;
                _logger.LogError($"Success executing job {job.Id}");
            }
            catch (Exception e)
            {
                _logger.LogError($"Exception executing job: {e}");
                jobResult.Status = -1;
            }

            /*
             * Report back.
             */
            var reportRes = await _hannibalClient.ReportJobAsync(new()
                { JobId = job.Id, Status = jobResult.Status, Owner = _ownerId });
        }
        catch (Exception e)
        {
            _logger.LogError($"Exception getting job: {e}");
        }
    }
}
