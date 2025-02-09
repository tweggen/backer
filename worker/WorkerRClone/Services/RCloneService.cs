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

    private Process? _processRClone;
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
    
    
    public override void Dispose()
    {
        _processRClone?.Kill();
        _processRClone?.Dispose();
        base.Dispose();
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
        /*
         * We need to create a list of jobs we already reported back to the caller
         * but that still are in rclone's queue.
         */
        List<int> listReportedJobs = new();
        lock (_lo)
        {
            mapJobs = new SortedDictionary<int, Job>(_mapRCloneToJob);
        }

        var rcloneClient = new RCloneClient(_rcloneHttpClient);

        foreach (var kvp in mapJobs)
        {
            int rcloneJobId = kvp.Key;
            Job job = kvp.Value;
            int jobId = job.Id;
            
            var jobStatus = await rcloneClient.GetJobStatusAsync(rcloneJobId, cancellationToken);
            if (jobStatus.finished)
            {
                if (jobStatus.success)
                {
                    /*
                     * Report back the job success.
                     */
                    var reportRes = await _hannibalClient.ReportJobAsync(new()
                        { JobId = jobId, Status = 0, Owner = _ownerId });
                    listReportedJobs.Add(rcloneJobId);
                }
                else
                {
                    _logger.LogWarning("Unable to perform job {jobId} from {sourceEndpoint} to {destEndpoint} : {error}.",
                        jobId, job.SourceEndpoint, job.DestinationEndpoint, jobStatus.error);

                    /*
                     * Report back the error.
                     */
                    var reportRes = await _hannibalClient.ReportJobAsync(new()
                        { JobId = jobId, Status = -1, Owner = _ownerId });
                    listReportedJobs.Add(rcloneJobId);
                }
            }
        }

        lock (_lo)
        {
            foreach (int deadRcloneJobId in listReportedJobs)
            {
                _mapRCloneToJob.Remove(deadRcloneJobId);
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
            
            AsyncResult? asyncResult;
            switch (/* job.Operation */ Rule.RuleOperation.Nop)
            {
                case Rule.RuleOperation.Copy:
                    asyncResult = await rcloneClient.CopyAsync(sourceUri, destinationUri, CancellationToken.None);
                    break;
                default:
                    asyncResult = new() { jobid = 0 };
                    break;
                case Rule.RuleOperation.Nop:
                    asyncResult = await rcloneClient.NoopAsync(CancellationToken.None);
                    break;
                case Rule.RuleOperation.Sync:
                    asyncResult = await rcloneClient.SyncAsync(sourceUri, destinationUri, CancellationToken.None);
                    break;

            }
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

        string? urlRClone = null;
        
        if (_processRClone == null)
        {
            _logger.LogWarning("rclone did not start at all, trying to use an already started instance");
            
            // TXWTODO: I accept this right now, as long I can reach it
            urlRClone = "http://localhost:5572";
        }
        else
        {
            StreamReader reader = _processRClone.StandardError;
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

        }

        if (null == urlRClone)
        {
            _logger.LogError("UNable to find an rclone url");
            throw new InvalidOperationException("UNable to get an rclone url.");
        }
        
        _rcloneHttpClient = new HttpClient() { BaseAddress = new Uri($"http://{urlRClone}") };
        var byteArray = new UTF8Encoding().GetBytes("who:how");
        _rcloneHttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));
        
        _hannibalConnection.On("NewJobAvailable", async () =>
        {
            await _triggerFetchJob();
        });

    }

}
