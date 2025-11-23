using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Hannibal.Client;
using Hannibal.Models;
using Hannibal.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tools;
using WorkerRClone.Client;
using WorkerRClone.Client.Models;
using WorkerRClone.Configuration;
using WorkerRClone.Models;
using Result = WorkerRClone.Models.Result;

namespace WorkerRClone;

public class RCloneService : BackgroundService
{
    private Models.RCloneServiceState.ServiceState _serviceState = RCloneServiceState.ServiceState.Starting;

    enum PendingRequest
    {
        None,
        Start,
        Stop
    }
    
    private PendingRequest _lastPendingRequest = PendingRequest.None;
    private bool _wasUserStop = false;
    
    private static object _classLock = new();
    private static int _nextId;

    private object _lo = new();
    
    private string _ownerId;
    private int _nRunningJobs = 0;
    
    private ILogger<RCloneService> _logger;
    private ProcessManager _processManager;
    private HubConnection _hannibalConnection;
    private bool _isConnectionSubscribed = false;

    private RCloneServiceOptions? _options = null;
    private bool _areOptionsValid = true;

    private Process? _processRClone;
    private HttpClient? _rcloneHttpClient;

    private SortedDictionary<int, Job> _mapRCloneToJob = new();
    
    private readonly Channel<RCloneServiceParams> _taskChannel = Channel.CreateUnbounded<RCloneServiceParams>();
    
    private bool _isStarted = false;
    
    private readonly IServiceScopeFactory _serviceScopeFactory;

    private const string _defaultRCloneUrl = "http://localhost:5572";

    private HashSet<string> _setRemotes = new();
    
    public RCloneService(
        ILogger<RCloneService> logger,
        ProcessManager processManager,
        IOptionsMonitor<RCloneServiceOptions> optionsMonitor,
        Dictionary<string, HubConnection> connections,
        IServiceScopeFactory serviceScopeFactory,
        ConfigHelper<RCloneServiceOptions> configHelper)
    {
        lock (_classLock)
        {
            _ownerId = $"worker-rclone-{_nextId++}";
        }
        _logger = logger;
        _logger.LogInformation($"RCloneService: Starting {_ownerId}.");
        
        _processManager = processManager;
        _options = optionsMonitor.CurrentValue;
        optionsMonitor.OnChange(async updated =>
        {
            _logger.LogInformation("RCloneService: Options changed.");

            if (_serviceState == RCloneServiceState.ServiceState.WaitConfig
                ||_serviceState == RCloneServiceState.ServiceState.WaitStart)
            {
                _options = updated;
                /*
                 * Note: CheckConfig will also start the operation if autostart
                 * has been set by the update.
                 */
                await _checkConfig();
            }
            else
            {
                _logger.LogError($"RCloneService: Options changed while not in WaitConfig state (but {_serviceState}. This is not supported. Ignoring.");
            }
        });
        _hannibalConnection = connections["hannibal"];
        _serviceScopeFactory = serviceScopeFactory;
    }
    
    
    public override void Dispose()
    {
        _processRClone?.Kill();
        _processRClone?.Dispose();
        base.Dispose();
    }


    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("RCloneService: Starting ExecuteAsync.");
        bool wasStart = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            switch (_serviceState)
            {
                case RCloneServiceState.ServiceState.Starting:
                case RCloneServiceState.ServiceState.CheckOnline:
                case RCloneServiceState.ServiceState.CheckRCloneProcess:
                case RCloneServiceState.ServiceState.WaitConfig:
                case RCloneServiceState.ServiceState.StartRCloneProcess:
                case RCloneServiceState.ServiceState.Exiting:
                    /*
                     * No action required,
                     */
                    break;
                
                case RCloneServiceState.ServiceState.Running:
                    /*
                     * If we transitioned to start and haven't started before, do now.
                     */
                    if (!wasStart)
                    {
                        wasStart = true;
                        try
                        {
                            // rCloneServiceParams = await _taskChannel.Reader.ReadAsync(cancellationToken);
                            /*
                             * Initially, we trigger reading all matching todos from hannibal.
                             * Whatever we got we execute.
                             * If we have nothing, we sleep until receiving an signalr update.
                             */
                            _triggerFetchJob(cancellationToken);
                        }
                        catch (Exception e)
                        {
                            wasStart = false;
                            _logger.LogError($"Exception while Startup in Execute Async: {e}");
                        }
                    }

                    break;
            }
            try
            {
                await _checkFinishedJobs(cancellationToken);
            }
            catch (Exception e)
            {
                _logger.LogError($"Exception while checking for finished jobs in Execute Async: {e}");
            }

            await Task.Delay(5_000, cancellationToken);
        }
        _logger.LogInformation("RCloneService: Exiting ExecuteAsync.");
    }


    private async Task _checkFinishedJobs(CancellationToken cancellationToken)
    {
        SortedDictionary<int, Job> mapJobs;
        /*
         * We need to create a list of jobs we already reported back to the caller
         * but that still are in rclone's queue.
         */
        List<int> listDoneJobs = new();
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
            
            /*
             * We need to wrap this into try/catch in case the job has ceased to
             * exist.
             */
            try
            {

                var jobStatus = await rcloneClient.GetJobStatusAsync(rcloneJobId, cancellationToken);
                if (jobStatus.finished)
                {
                    if (jobStatus.success)
                    {
                        _logger.LogInformation(
                            "job success {jobId} from {sourceEndpoint} to {destEndpoint} : {jobStatus}.",
                            jobId, job.SourceEndpoint, job.DestinationEndpoint, jobStatus);

                        /*
                         * Report back the job success.
                         */
                        using var scope = _serviceScopeFactory.CreateScope();
                        var hannibalService = scope.ServiceProvider.GetRequiredService<IHannibalServiceClient>();
                        var reportRes = await hannibalService.ReportJobAsync(new()
                                { JobId = jobId, State = Job.JobState.DoneSuccess, Owner = _ownerId },
                            cancellationToken);
                        listDoneJobs.Add(rcloneJobId);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Unable to perform job {jobId} from {sourceEndpoint} to {destEndpoint} : {error}.",
                            jobId, job.SourceEndpoint.Path, job.DestinationEndpoint.Path, jobStatus.error);

                        /*
                         * Report back the error.
                         */
                        using var scope = _serviceScopeFactory.CreateScope();
                        var hannibalService = scope.ServiceProvider.GetRequiredService<IHannibalServiceClient>();
                        var reportRes = await hannibalService.ReportJobAsync(new()
                                { JobId = jobId, State = Job.JobState.DoneFailure, Owner = _ownerId },
                            cancellationToken);
                        listDoneJobs.Add(rcloneJobId);
                    }
                }
                else
                {
                    _logger.LogInformation($"Job Status for {jobId} is {jobStatus}.");

                    // TXWTODO: Throttle the amount of reportjobasync calls.
                    using var scope = _serviceScopeFactory.CreateScope();
                    var hannibalService = scope.ServiceProvider.GetRequiredService<IHannibalServiceClient>();
                    var reportRes = await hannibalService.ReportJobAsync(new()
                            { JobId = jobId, State = Job.JobState.Executing, Owner = _ownerId },
                        cancellationToken);

                }
            }
            catch (Exception ex)
            {
                /*
                 * We can igore this exception because the job might have ceased to exist.
                 */
            }
        }

        lock (_lo)
        {
            foreach (int deadRcloneJobId in listDoneJobs)
            {
                _mapRCloneToJob.Remove(deadRcloneJobId);
            }
        }
    }
    
    
    private async Task<AsyncResult> _startJob(Job job, CancellationToken cancellationToken)
    {
        var rcloneClient = new RCloneClient(_rcloneHttpClient);
        try
        {
            _logger.LogInformation($"Starting job {job.Id}");
            
            /*
             * Resolve the endpoints.
             */
            var sourceEndpoint = job.SourceEndpoint;
            var destinationEndpoint = job.DestinationEndpoint;

            string sourceUri = $"{sourceEndpoint.Storage.UriSchema}:/{sourceEndpoint.Path}";
            string destinationUri = $"{destinationEndpoint.Storage.UriSchema}:/{destinationEndpoint.Path}";
            
            _logger.LogInformation($"sourceUri is {sourceUri}");
            _logger.LogInformation($"destinationUri is {destinationUri}");
            
            AsyncResult? asyncResult;
            switch (job.Operation /* Rule.RuleOperation.Nop */)
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
    

    private async Task _triggerFetchJob(CancellationToken cancellationToken)
    {
        _logger.LogDebug("RCloneService: _triggerFetchJob called.");
        if (_serviceState != RCloneServiceState.ServiceState.Running)
        {
            _logger.LogDebug($"RCloneService: Spurious call of _triggerFetchJob in state {_serviceState}, ignoring.");
            return;
        }
        
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
                    _logger.LogDebug("RCloneService: Too many parallel jobs, refusing to fetch more.");
                    return;
                }
            }

            /*
             * Then get the next job.
             */
            Job? job;
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var hannibalService = scope.ServiceProvider.GetRequiredService<IHannibalServiceClient>();
                job = await hannibalService.AcquireNextJobAsync(
                    new()
                    {
                        Username = "timo.weggen@gmail.com",
                        Capabilities = String.Join(",", _setRemotes.ToList()),
                        Owner = _ownerId
                    },
                    cancellationToken);
            }
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
                var asyncResult = await _startJob(job, cancellationToken);
                _logger.LogInformation($"Started executing job {job.Id}");
                
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
                using var scope = _serviceScopeFactory.CreateScope();
                var hannibalService = scope.ServiceProvider.GetRequiredService<IHannibalServiceClient>();
                var reportRes = await hannibalService.ReportJobAsync(new()
                    { JobId = job.Id, State = Job.JobState.DoneFailure, Owner = _ownerId },
                    cancellationToken);
            }

        }
        catch (Exception e)
        {
            _logger.LogError($"Exception getting job: {e}");
        }
    }
    

    private string _decodePath(string orgPath)
    {
        if (orgPath.Length > 2)
        {
            if (orgPath.StartsWith("~/") || orgPath.StartsWith("~\\"))
            {
                return _decodePath(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        orgPath.Substring(2)
                    ));
            }
            else
            {
                return orgPath;
            }
        }
        else
        {
            return orgPath;
        }
    }


    private async void _readPrintLog(StreamReader readerStdErr, CancellationToken cancellationToken)
    {
        while (!_processRClone.HasExited)
        {
            string? message = await readerStdErr.ReadLineAsync(cancellationToken);
            if (null == message)
            {
                _logger.LogInformation("rclone log terminates.");
                break;
            }
            _logger.LogInformation($"rclone: {message}");
        }
        _logger.LogInformation("rclone terminates.");
    }


    private async Task _startRCloneProcess(CancellationToken cancellationToken)
    {
        if (null == _options)
        {
            throw new InvalidOperationException("RCloneService: No options available.");
        }
        var startInfo = new ProcessStartInfo()
        {
            FileName =  _decodePath(_options.RClonePath),
            Arguments = _options.RCloneOptions,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        _processRClone = CrossPlatformProcessManager.StartManagedProcess(startInfo);

        string? urlRClone = null;
        
        if (_processRClone != null)
        {
            StreamReader reader = _processRClone.StandardError;
            string strErrorOutput = "";
            Regex reUrl = new("http://(?<url>[1-9][0-9]*\\.[0-9][1-9]*\\.[0-9][1-9]*\\.[0-9][1-9]*:[1-9][0-9]*)/");
            while (true)
            {
                if (_processRClone.HasExited)
                {
                    _logger.LogError($"rclone exited with error {strErrorOutput}" );
                    // throw new InvalidOperationException("rclone exited with error: ");
                    break;
                }
                string? output = await reader.ReadLineAsync(cancellationToken);
                if (null == output)
                {
                    _logger.LogError($"rclone did not start with expected output but {strErrorOutput}" );
                    throw new InvalidOperationException("rclone did not start with the expected output,");
                }

                strErrorOutput += output + "\n";
                Match match = reUrl.Match(output);
                if (match.Success)
                {
                    urlRClone = match.Groups["url"].Value;
                    _logger.LogInformation($"rclone: {strErrorOutput}");
                    break;
                }
            }

            Task.Run(() => _readPrintLog(reader, cancellationToken));
        }


        if (null == urlRClone)
        {
            _logger.LogWarning("rclone did not start at all, trying to use an already started instance");
            urlRClone = _defaultRCloneUrl;
        }
    }


    /**
     * Test if we have an rclone process running using our authentication scheme.
     * This creates an http service, leaving it in useful condition if the test was
     * successful.
     * Also download the list of remotes supported by rsync.
     */
    private async Task<bool> _haveRCloneProcess(string urlRClone)
    {
        _rcloneHttpClient = new HttpClient() { BaseAddress = new Uri(urlRClone) };
        var byteArray = new UTF8Encoding().GetBytes("who:how");
        _rcloneHttpClient.DefaultRequestHeaders.Authorization = 
            new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(byteArray));
        var rcloneClient = new RCloneClient(_rcloneHttpClient);

        bool haveRClone = false;
        try
        {
            var listRemotesResult = await rcloneClient.ListRemotesAsync(CancellationToken.None);
            _logger.LogInformation($"RCloneService: found working rclone instance with remotes: {listRemotesResult}");
            haveRClone = true;
            foreach (var remote in listRemotesResult.remotes)
            {
                try
                {
                    _setRemotes.Add(remote);
                }
                catch (Exception e)
                {
                    _logger.LogError($"RCloneService: Error adding remote, ignoring. {remote}: {e}");
                }
            }
        }
        catch (Exception e)
        {
            
        }

        if (!haveRClone)

        {
            _rcloneHttpClient.Dispose();
            _rcloneHttpClient = null;
        }

        return haveRClone;
    }


    /**
     * We are running and supposed to wait until the jobs are done.
     */
    private async Task _toWaitStop()
    {
        _serviceState = RCloneServiceState.ServiceState.WaitStop;
        _logger.LogInformation("RCloneService: Waiting for stop request.");
        
        var rcloneClient = new RCloneClient(_rcloneHttpClient);
        var jobList = await rcloneClient.GetJobListAsync(CancellationToken.None);
        List<int>? list = null;

        if (jobList.running_ids != null)
        {
            list = jobList.running_ids;
        }

        if (list != null)
        {
            foreach (var jobid in list)
            {
                _logger.LogInformation($"RCloneService: Stopping job {jobid}");
                await rcloneClient.StopJobAsync(CancellationToken.None);
            }
        }

        await _toWaitStart();
    }


    private void _toWaitConfig()
    {
        _serviceState = RCloneServiceState.ServiceState.WaitConfig;
        _logger.LogInformation("RCloneService: Waiting for configuration.");
        
        /*
         * We do not actively act, just wait for the REST put call.
         */
    }


    private async Task _toStartRClone()
    {
        _serviceState = RCloneServiceState.ServiceState.StartRCloneProcess;
        _logger.LogInformation("RCloneService: Starting rclone process.");
        try
        {
            /*
             * Start rclone process, wait until we can access the rest interface.
             */
            await _startRCloneProcess(CancellationToken.None);
            bool haveRCloneProcess = false;

            int nTries = 10;
            while (--nTries > 0)
            {
                haveRCloneProcess = await _haveRCloneProcess(_defaultRCloneUrl);;
                if (haveRCloneProcess) break;
                _logger.LogWarning("RCloneService: waiting for rest interface to become available.");
                await Task.Delay(1000);
            }

            if (!haveRCloneProcess)
            {
                _areOptionsValid = false;
                _logger.LogError("RCloneService: rclone process did not start.");
                _toWaitConfig();
                return;
            }   
        
            _logger.LogInformation("RCloneService: rclone process started.");
            await _toWaitStart();
        }
        catch (Exception e)
        {
            _areOptionsValid = false;
            _logger.LogError($"Exception while starting rclone: {e}");
            _toWaitConfig();
            return;
        }
    }


    private async Task _toRunning()
    {
        _serviceState = RCloneServiceState.ServiceState.Running;
        _logger.LogInformation("RCloneService: Running.");

        /*
        * Now we definitely transition to running.
        * So this is no user forced stop anymore. 
        */
        _wasUserStop = false;
        
        /*
         * Start the actual operation.
         * Unfortunately we cannot unsubscribe from this subscription, sp we
         * need to check, if the connection is desired.
         */
        if (!_isConnectionSubscribed)
        {
            _hannibalConnection.On("NewJobAvailable", async () =>
            {
                if (_serviceState == RCloneServiceState.ServiceState.Running)
                {
                    await _triggerFetchJob(CancellationToken.None);
                }
            });
        }
    }


    /**
     * We are ready to go and just need a start request or an
     * autostart option.
     */
    private async Task _toWaitStart()
    {
        _serviceState = RCloneServiceState.ServiceState.WaitStart;
        _logger.LogInformation("RCloneService: ToWaitStart");
        
        /*
         * Must not happen, checked in previous state.
         */
        if (_options == null)
        {
            _toWaitConfig();
        }
        
        /*
         * Has stop been triggered in the meantime?
         */
        if (_lastPendingRequest == PendingRequest.Stop)
        {
            _lastPendingRequest = PendingRequest.None;
            await _toWaitStop();
        }
        
        /*
         * Shall we transition directly to running without waiting
         * for a start request?
         */
        bool triggerStart = false;
        if (_lastPendingRequest == PendingRequest.Start)
        {
            _lastPendingRequest = PendingRequest.None;
            triggerStart = true;
        }

        if (_options!.Autostart && !_wasUserStop)
        {
            triggerStart = true;
        }
        
        /*
         * So, if we think we should start immediately, do it.
         */
        if (triggerStart)
        {
            await _toRunning();
        }
        else
        {
            _logger.LogInformation("RCloneService: Waiting for explicit start request.");
        }
    }
    
    
    private async Task _toCheckRCloneProcess()
    {
        _serviceState = RCloneServiceState.ServiceState.CheckRCloneProcess;
        _logger.LogInformation("RCloneService: Checking rclone process.");
        bool haveRCloneProcess = await _haveRCloneProcess(_defaultRCloneUrl);
        if (!haveRCloneProcess)
        {
            await _toStartRClone();
        }
        else
        {
            await _toWaitStart();
        }
    }
    

    private async Task _toCheckOnline()
    {
        _serviceState = RCloneServiceState.ServiceState.CheckOnline;
        _logger.LogInformation("RCloneService: Checking online.");
        
        try {
            using var scope = _serviceScopeFactory.CreateScope();
            var hannibalService = scope.ServiceProvider.GetRequiredService<IHannibalServiceClient>();
            var user = await hannibalService.GetUserAsync(0, CancellationToken.None);
            
            /*
             * OK, no exception, online connection works. So progress.
             */
            await _toCheckRCloneProcess();

        } catch (Exception e) {
            _logger.LogError($"Exception while checking online: {e}");

            _areOptionsValid = false;
            _toWaitConfig();
        }

    }


    private async Task _checkConfig()
    {
        _logger.LogInformation("RCloneService: Checking configuration.");
        if (null == _options)
        {
            _logger.LogWarning("RCloneService: No configuration at all.");
        
            _toWaitConfig();
            return;
        }

        if (!_areOptionsValid)
        {
            _logger.LogWarning("RCloneService: Invalidated configuration found.");
        
            _toWaitConfig();
            return;
        }
        
        if (String.IsNullOrWhiteSpace(_options.BackerUsername)
            || String.IsNullOrWhiteSpace(_options.BackerPassword)
            || String.IsNullOrWhiteSpace(_options.RClonePath)
            || String.IsNullOrWhiteSpace(_options.RCloneOptions)
            || String.IsNullOrWhiteSpace(_options.UrlSignalR))
        {
            _logger.LogWarning("RCloneService: Configuration incomplete.");
        
            _toWaitConfig();
            return;
        }
        
        /*
         * Configuration appears to be valid. Progress to the next step.
         */
        await _toCheckOnline();

    }


    /**
     * If not running yet, start rclone job processing.
     */
    public async Task StartJobsAsync(CancellationToken cancellationToken)
    {
        switch (_serviceState)
        {
            case RCloneServiceState.ServiceState.Starting:
            case RCloneServiceState.ServiceState.WaitConfig:
            case RCloneServiceState.ServiceState.CheckOnline:
            case RCloneServiceState.ServiceState.CheckRCloneProcess:
            case RCloneServiceState.ServiceState.StartRCloneProcess:
                /*
                 * Still booting up, remember request.
                 */
                _lastPendingRequest = PendingRequest.Start;
                break;
            
            case RCloneServiceState.ServiceState.WaitStart:
                /*
                 * Already started up, kick it.
                 * But clear any contradicting requets.
                 */
                _lastPendingRequest = PendingRequest.None;
                await _toRunning();
                break;
            
            case RCloneServiceState.ServiceState.Running:
                /*
                 * Already running, ignore request.
                 */
                break;

            case RCloneServiceState.ServiceState.WaitStop:
                /*
                 * Remember request to restart.
                 */
                _lastPendingRequest = PendingRequest.Start;
                break;
            
            case RCloneServiceState.ServiceState.Exiting:
                /*
                 * Ignore, we are shutting down.
                 */
                break;
        }
    }


    /**
     * If running, stop rclone job processing.
     */
    public async Task StopJobsAsync(CancellationToken cancellationToken)
    {
        _wasUserStop = true;
        
        switch (_serviceState)
        {
            case RCloneServiceState.ServiceState.Starting:
            case RCloneServiceState.ServiceState.WaitConfig:
            case RCloneServiceState.ServiceState.CheckOnline:
            case RCloneServiceState.ServiceState.CheckRCloneProcess:
            case RCloneServiceState.ServiceState.StartRCloneProcess:
                /*
                 * Still booting up, remember request.
                 */
                // TXWTODO: What should that do with autostart enabled?
                _lastPendingRequest = PendingRequest.Stop;
                break;
            
            case RCloneServiceState.ServiceState.WaitStart:
                /*
                 * Not started at all.
                 * Clear any contradicting requets.
                 */
                _lastPendingRequest = PendingRequest.None;
                break;
            
            case RCloneServiceState.ServiceState.Running:
                /*
                 * Ask to shup down, clear any pending requests.
                 */
                _lastPendingRequest = PendingRequest.None;
                await _toWaitStop();
                break;

            case RCloneServiceState.ServiceState.WaitStop:
                /*
                 * Already stopping
                 */
                _lastPendingRequest = PendingRequest.None;
                break;
            
            case RCloneServiceState.ServiceState.Exiting:
                /*
                 * Ignore, we are shutting down.
                 */
                break;
        }
    }
    
    
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation($"StartAsync: Starting RCloneService with options {_options}");

        if (_isStarted)
        {
            throw new InvalidOperationException("Already started.");
        }
        _isStarted = true;

        await base.StartAsync(cancellationToken);

        /*
         * Initially, we wait for the configuration
         * to arrive.
         */
        _serviceState = RCloneServiceState.ServiceState.WaitConfig;
        await _checkConfig();
    }


    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("StopAsync called");

        bool wasStarted = _isStarted;
        if (!wasStarted)
        {
            return;
        }
        
        _isStarted = false;
        
        if (_processRClone != null)
        {
            var processRClone = _processRClone;
            _processRClone = null;
            processRClone.Dispose();
        }

        if (wasStarted)
        {
            await base.StopAsync(cancellationToken);
        }
    }


    public RCloneServiceState GetState()
    {
        return new RCloneServiceState()
        {
            State = _serviceState,
            StateString = _serviceState.ToString()
        };
    }
}
