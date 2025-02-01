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
using WorkerRClone.Client.Models;
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

    private SortedDictionary<int, Job> _mapRCloneToJob = new();
    
    
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
    
    
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        /*
         * Initially, we trigger reading all matching todos from hannibal.
         * Whatever we got we execute.
         * If we have nothing, we sleep until receiving an signalr update.
         */
        _triggerFetchJob();
        
        while (!cancellationToken.IsCancellationRequested)
        {
            // using IServiceScope scope = _serviceScopeFactory.CreateScope();
            
            // var context = scope.ServiceProvider.GetRequiredService<HannibalContext>();
            
            // await _rules2Jobs(context, cancellationToken);
            
            await _checkFinishedJobs(cancellationToken);
            await Task.Delay(5_000, cancellationToken);
        }
    }


    private async Task _checkFinishedJobs(CancellationToken cancellationToken)
    {
        SortedDictionary<int, Job> mapJobs;
        lock (_lo)
        {
            mapJobs = new SortedDictionary<int, Job>(_mapRCloneToJob);
        }

        var rcloneClient = new RCloneClient(_rcloneHttpClient);

        foreach (var kvp in mapJobs)
        {
            var jobStatus = await rcloneClient.GetJobStatusAsync(kvp.Key, cancellationToken);
            if (jobStatus.finished)
            {
                if (jobStatus.success)
                {
                    /*
                     * Report back the job success.
                     */
                    var reportRes = await _hannibalClient.ReportJobAsync(new()
                        { JobId = kvp.Value.Id, Status = 0, Owner = _ownerId });
                    #if false
ail: Microsoft.Extensions.Hosting.Internal.Host[9]
      BackgroundService failed
      System.Net.Http.HttpRequestException: Response status code does not indicate success: 500 (Internal Server Error).
         at System.Net.Http.HttpResponseMessage.EnsureSuccessStatusCode()
         at Hannibal.Client.HannibalServiceClient.ReportJobAsync(JobStatus jobStatus) in C:\Users\timow\coding\github\backer\application\Hannibal\Client\HannibalServiceClient.cs:line 49
         at WorkerRClone.RCloneService._checkFinishedJobs(CancellationToken cancellationToken) in C:\Users\timow\coding\github\backer\worker\WorkerRClone\Services\RCloneService.cs:line 108
         at WorkerRClone.RCloneService.ExecuteAsync(CancellationToken cancellationToken) in C:\Users\timow\coding\github\backer\worker\WorkerRClone\Services\RCloneService.cs:line 82
         at Microsoft.Extensions.Hosting.Internal.Host.TryExecuteBackgroundServiceAsync(BackgroundService backgroundService)
crit: Microsoft.Extensions.Hosting.Internal.Host[10]
      The HostOptions.BackgroundServiceExceptionBehavior is configured to StopHost. A BackgroundService has thrown an unhandled exception, and the IHost instance is stopping. To avoid this behavior, configure this to Ignore; however the BackgroundService will not be restarted.
      System.Net.Http.HttpRequestException: Response status code does not indicate success: 500 (Internal Server Error).
         at System.Net.Http.HttpResponseMessage.EnsureSuccessStatusCode()
         at Hannibal.Client.HannibalServiceClient.ReportJobAsync(JobStatus jobStatus) in C:\Users\timow\coding\github\backer\application\Hannibal\Client\HannibalServiceClient.cs:line 49
         at WorkerRClone.RCloneService._checkFinishedJobs(CancellationToken cancellationToken) in C:\Users\timow\coding\github\backer\worker\WorkerRClone\Services\RCloneService.cs:line 108
         at WorkerRClone.RCloneService.ExecuteAsync(CancellationToken cancellationToken) in C:\Users\timow\coding\github\backer\worker\WorkerRClone\Services\RCloneService.cs:line 82
         at Microsoft.Extensions.Hosting.Internal.Host.TryExecuteBackgroundServiceAsync(BackgroundService backgroundService)
info: Microsoft.Hosting.Lifetime[0]
      Application is shutting down...
#endif
                }
                else
                {
                    /*
                     * Report back the error.
                     */
                    var reportRes = await _hannibalClient.ReportJobAsync(new()
                        { JobId = kvp.Value.Id, Status = -1, Owner = _ownerId });
                }
            }
        }
    }
    
    
    private async Task<AsyncResult> _startJob(Job job)
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
            
            var asyncResult = await rcloneClient.CopyAsync(sourceUri, destinationUri, CancellationToken.None);
            lock (_lo)
            {
                _mapRCloneToJob.Add(asyncResult.jobid, job);
            }
            return asyncResult;
        }
        catch (Exception e)
        {
            _logger.LogError($"Exception while sync: {e}");
            throw e;
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
                var asyncResult = await _startJob(job);
                _logger.LogError($"Started executing job {job.Id}");
                
                /*
                 * Job is running, we will poll the result-
                 */
            }
            catch (Exception e)
            {
                _logger.LogError($"Exception executing job: {e}");

                /*
                 * Report back the error.
                 */
                var reportRes = await _hannibalClient.ReportJobAsync(new()
                    { JobId = job.Id, Status = -1, Owner = _ownerId });
            }

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
