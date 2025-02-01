using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Hannibal.Client;
using Hannibal.Models;
using Higgins.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tools;
using WorkerRClone.Client;
using WorkerRClone.Configuration;
using Result = WorkerRClone.Models.Result;

namespace WorkerRClone;

public class RCloneService : BackgroundService
{
    private static object _classLock = new();
    private static int _nextId;

    private object _lo = new();
    
    private string _ownerId;
    private int _nRunningJobs = 0;
    
    private ILogger<RCloneService> _logger;
    private ProcessManager _processManager;
    private HubConnection _hannibalConnection;
    private IHannibalServiceClient _hannibalClient;
    private IHigginsServiceClient _higginsClient;

    private readonly RCloneServiceOptions _options;

    private Process _processRClone;
    private HttpClient _rcloneHttpClient;
    
    public RCloneService(
        ILogger<RCloneService> logger,
        ProcessManager processManager,
        IOptions<RCloneServiceOptions> options,
        Dictionary<string, HubConnection> connections,
        IHannibalServiceClient hannibalClient,
        IHigginsServiceClient higginsClient)
    {
        lock (_classLock)
        {
            _ownerId = $"worker-rclone-{_nextId++}";
        }
        _logger = logger;
        _processManager = processManager;
        _options = options.Value;
        _hannibalConnection = connections["hannibal"];
        _hannibalClient = hannibalClient;
        _higginsClient = higginsClient;
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


    private async Task<Result> _startJob(Job job)
    {
        var rcloneClient = new RCloneClient(_rcloneHttpClient);
        try
        {
            _logger.LogInformation($"Starting job {job.Id}");
            
            /*
             * Resolve the endpoints.
             */
            var sourceEndpoint = await _higginsClient.GetEndpointAsync(job.SourceEndpoint);
            var destinationEndpoint = await _higginsClient.GetEndpointAsync(job.DestinationEndpoint);

            string sourceUri = $"{sourceEndpoint.Storage.UriSchema}:/{sourceEndpoint.Path}";
            string destinationUri = $"{destinationEndpoint.Storage.UriSchema}:/{destinationEndpoint.Path}";
            
            _logger.LogInformation($"sourceUri is {sourceUri}");
            _logger.LogInformation($"destinationUri is {destinationUri}");
            
            var res = await rcloneClient.CopyAsync(sourceUri, destinationUri, CancellationToken.None);
            return new() { Status = 0 };
        }
        catch (Exception e)
        {
            _logger.LogError($"Exception while sync: {e}");
            return new() { Status = -1 };
        }
    }
    

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _processRClone.Dispose();
        await base.StopAsync(cancellationToken);
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
                new() { Capabilities ="rclone", Owner = _ownerId });
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
                var status = await _startJob(job);
                if (status.Status == 0)
                {
                    jobResult.Status = 0;
                    _logger.LogError($"Success executing job {job.Id}");
                }
                else
                {
                    jobResult.Status = -1;
                    _logger.LogError($"Error executing job {job.Id}");
                }
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

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);

        _processRClone = _processManager.StartManagedProcess(new ProcessStartInfo()
            {
                FileName = _options.RClonePath,
                Arguments = _options.RCloneOptions,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        );

        if (_processRClone == null)
        {
            _logger.LogError("rclone did not start at all.");
            throw new InvalidOperationException("rclone did not start at all.");
        }
        
        StreamReader reader = _processRClone.StandardError;
        string? urlRClone = null;
        string strErrorOutput = "";
        Regex reUrl = new("http://(?<url>[1-9][0-9]*\\.[0-9][1-9]*\\.[0-9][1-9]*\\.[0-9][1-9]*:[1-9][0-9]*)/");
        while (true)
        {
            if (_processRClone.HasExited)
            {
                _logger.LogError($"rclone exited with error {strErrorOutput}" );
                throw new InvalidOperationException("rclone exited with error: ");
            }
            string? output = await reader.ReadLineAsync();
            if (null == output)
            {
                _logger.LogError($"rclone did not start with expected output but {strErrorOutput}" );
                throw new InvalidOperationException("rclone did not start with the expected output,");
            }

            strErrorOutput += output;
            Match match = reUrl.Match(output);
            if (match.Success)
            {
                urlRClone = match.Groups["url"].Value;
                break;
            }
        }

        _rcloneHttpClient = new HttpClient() { BaseAddress = new Uri($"http://{urlRClone}") };
        var byteArray = new UTF8Encoding().GetBytes("who:how");
        _rcloneHttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));
        
        _hannibalConnection.On<Job>("NewJobAvailable", (message) =>
        {
            Console.WriteLine($"Received message: {message}");
        });

    }

    public override void Dispose()
    {
        _processRClone?.Kill();
        _processRClone?.Dispose();
        base.Dispose();
    }
}
