using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Hannibal;
using Hannibal.Client;
using Hannibal.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OAuth2.Client;
using Tools;
using WorkerRClone.Client;
using WorkerRClone.Client.Models;
using WorkerRClone.Configuration;
using WorkerRClone.Models;

namespace WorkerRClone.Services;

public class RCloneService : BackgroundService
{
    internal RCloneServiceState _state = new();
    private RCloneStateMachine? _stateMachine;
    
    internal bool _wasUserStop = false;
    
    private static object _classLock = new();
    private static int _nextId;

    private object _lo = new();
    
    private string _ownerId;
    private int _nRunningJobs = 0;
    
    internal ILogger<RCloneService> _logger;
    private ProcessManager _processManager;
    private HubConnection _hannibalConnection;  // CLIENT role: connects to Hannibal
    private bool _isConnectionSubscribed = false;

    private RCloneServiceOptions? _options = null;
    internal bool _areOptionsValid = true;

    private Process? _processRClone;
    internal HttpClient? _rcloneHttpClient;

    private SortedDictionary<int, Job> _mapRCloneToJob = new();
    
    private readonly Channel<RCloneServiceParams> _taskChannel = Channel.CreateUnbounded<RCloneServiceParams>();
    
    private bool _isStarted = false;
    
    private readonly IServiceScopeFactory _serviceScopeFactory;

    private const string _defaultRCloneUrl = "http://localhost:5572";
    private readonly INetworkIdentifier _networkIdentifier;
    
    private RCloneConfigManager? _configManager = null;
    internal IReadOnlyList<Storage> _listStorages;
    internal RCloneStorages _rcloneStorages;
    
    // Callbacks for external notification (used by BackerControlHub)
    public Action<RCloneServiceState>? OnStateChanged { get; set; }
    public Action<TransferStatsResult>? OnTransferStatsChanged { get; set; }

    public RCloneService(
        ILogger<RCloneService> logger,
        ProcessManager processManager,
        RCloneStorages rcloneStorages,
        IOptionsMonitor<RCloneServiceOptions> optionsMonitor,
        Dictionary<string, HubConnection> connections,
        IServiceScopeFactory serviceScopeFactory,
        ConfigHelper<RCloneServiceOptions> configHelper,
        INetworkIdentifier networkIdentifier)
    {
        lock (_classLock)
        {
            _ownerId = $"worker-rclone-{_nextId++}";
        }
        
        _logger = logger;
        _logger.LogInformation($"RCloneService: Starting {_ownerId}.");
        
        _processManager = processManager;
        _options = optionsMonitor.CurrentValue;
        _rcloneStorages = rcloneStorages;
        
        optionsMonitor.OnChange(async updated =>
        {
            _logger.LogInformation($"RCloneService: options changed to {updated}.");

            if (_state.State == RCloneServiceState.ServiceState.Starting
                || _state.State == RCloneServiceState.ServiceState.WaitConfig
                || _state.State == RCloneServiceState.ServiceState.WaitStart)
            {
                _logger.LogInformation("Using options.");
                _options = updated;
                _areOptionsValid = true;
                
                /*
                 * Note: CheckConfig will also start the operation if autostart
                 * has been set by the update.
                 */
                await _checkConfig();
            }
            else
            {
                _logger.LogError($"RCloneService: Options changed while not in WaitConfig state (but {_state.State}. This is not supported. Ignoring.");
            }
        });
        
        _hannibalConnection = connections["hannibal"];
        _serviceScopeFactory = serviceScopeFactory;
        
        _networkIdentifier = networkIdentifier;
        _networkIdentifier.NetworkChanged += _onNetworkChanged;
        
        _logger.LogInformation($"RCloneService: Network initially is {_networkIdentifier.GetCurrentNetwork()}.");
    }


    private string _rcloneConfigFile()
    {
        return Path.Combine(
            Tools.EnvironmentDetector.GetConfigDir("Backer"),
            "backer-rclone.conf");
    }
    
    
    private string _rcloneConfigDir()
    {
        return Path.Combine(
            Tools.EnvironmentDetector.GetConfigDir("Backer"));
    }
    

    private void _onNetworkChanged(object sender, EventArgs e)
    {
        _logger.LogInformation($"RCloneService: Network changed to {(e as NetworkChangedEventArgs)!.NetworkName}.");
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
            switch (_state.State)
            {
                case RCloneServiceState.ServiceState.Starting:
                case RCloneServiceState.ServiceState.CheckOnline:
                case RCloneServiceState.ServiceState.BackendsLoggingIn:
                case RCloneServiceState.ServiceState.CheckRCloneProcess:
                case RCloneServiceState.ServiceState.WaitConfig:
                case RCloneServiceState.ServiceState.StartRCloneProcess:
                case RCloneServiceState.ServiceState.RestartingForReauth:
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
                            /*
                             * Initially, we trigger reading all matching todos from hannibal.
                             * Whatever we got we execute.
                             * If we have nothing, we sleep until receiving an signalr update.
                             */
                            _triggerFetchJobAsync(cancellationToken);
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


    /**
     * Take care we have all remotes that we need for this endpoint in the configuration.
     * If necessary, create them, restart rclone.
     */
    private async Task _ensureConfiguredEndpoint(
        EndpointState es, 
        CancellationToken cancellationToken)
    {
        _logger.LogDebug($"RCloneService: _configureRCloneStorage called for storage {es.Endpoint.Storage.UriSchema}.");

        if (!_isStarted)
        {
            _logger.LogWarning($"Asked to configure parameter although service is not started yet.");
            return;
        }

        if (null == _configManager)
        {
            _logger.LogWarning($"Called although config manager does not exuist.");
            return;
        }

        Storage storage = es.Endpoint.Storage;
        StorageState ss = await _rcloneStorages.FindStorageState(storage, cancellationToken);
        
        _configManager.AddOrUpdateRemote(storage.UriSchema, ss.RCloneParameters);
        _configManager.SaveToFile(_rcloneConfigFile());
    }


    public async Task<EndpointState> _createEndpointStateAsync(JobState js, 
        Hannibal.Models.Endpoint endpoint,
        CancellationToken cancellationToken)
    {
        string uri = $"{endpoint.Storage.UriSchema}:/{endpoint.Path}";
        EndpointState es = new()
        {
            Endpoint = endpoint,
            Uri = uri
        };

        /*
         * Ensure we have a valid configuration.
         */
        await _ensureConfiguredEndpoint(es, cancellationToken);
        
        return es;
    }
    
    
    private async Task<AsyncResult> _startJobAsync(JobState js, CancellationToken cancellationToken)
    {
        Job job = js.Job;
        
        try
        {
            _logger.LogInformation($"Starting job {job.Id}");

            js.SourceEndpointState = await _createEndpointStateAsync(js, job.SourceEndpoint, cancellationToken);
            js.DestinationEndpointState = await _createEndpointStateAsync(js, job.DestinationEndpoint, cancellationToken);
            
            _logger.LogInformation($"sourceUri is {js.SourceEndpointState.Uri}");
            _logger.LogInformation($"destinationUri is {js.DestinationEndpointState.Uri}");
            
            AsyncResult? asyncResult;
            switch (job.Operation /* Rule.RuleOperation.Nop */)
            {
                case Rule.RuleOperation.Copy:
                    asyncResult = await js.RCloneClient.CopyAsync(
                        js.SourceEndpointState.Uri, js.DestinationEndpointState.Uri, 
                        cancellationToken);
                    break;
                default:
                    asyncResult = new() { jobid = 0 };
                    break;
                case Rule.RuleOperation.Nop:
                    asyncResult = await js.RCloneClient.NoopAsync(cancellationToken);
                    break;
                case Rule.RuleOperation.Sync:
                    asyncResult = await js.RCloneClient.SyncAsync(
                        js.SourceEndpointState.Uri, js.DestinationEndpointState.Uri,
                        cancellationToken);
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


    private JobState _createJobState(Job job)
    {
        if (null == _rcloneHttpClient)
        {
            throw new InvalidOperationException("RCloneService: No http client available.");
        }
        
        var httpClient = _rcloneHttpClient;
        var rCloneClient = new RCloneClient(httpClient);

        JobState js = new()
        {
            Job = job,
            HttpClient = httpClient,
            RCloneClient = rCloneClient 
        };

        return js;
    }
    

    private async Task _triggerFetchJobAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("RCloneService: _triggerFetchJob called.");
        if (_state.State != RCloneServiceState.ServiceState.Running)
        {
            _logger.LogDebug($"RCloneService: Spurious call of _triggerFetchJob in state {_state.State}, ignoring.");
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
                        Capabilities = "use_me",
                        Owner = _ownerId,
                        Networks = _networkIdentifier?.GetCurrentNetwork() ?? "Unknown"
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

            JobState jobState = _createJobState(job);

            /*
             * Execute the job, remember the result.
             */
            try
            {
                /*
                 * Execute the job.
                 */
                var asyncResult = await _startJobAsync(jobState, cancellationToken);
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


    private async Task _fetchStorageOptions(CancellationToken cancellationToken)
    {
        /*
         * Update storage options. Not supported during runtime, we do it for each job
         * anyway.
         */
    }
    

    private async Task _startRCloneProcess(CancellationToken cancellationToken)
    {
        if (null == _options)
        {
            throw new InvalidOperationException("RCloneService: No options available.");
        }

        var options = $" --config=\"{_rcloneConfigFile()}\" " + _options.RCloneOptions;
        
        _logger.LogInformation($"Trying to start rclone from {_options.RClonePath}...");
        var startInfo = new ProcessStartInfo()
        {
            FileName =  _decodePath(_options.RClonePath),
            Arguments = options,
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
        }
        catch (Exception e)
        {
            _logger.LogInformation($"RCloneService: Unable to connect to rclone service.");
        }

        if (!haveRClone)

        {
            _rcloneHttpClient.Dispose();
            _rcloneHttpClient = null;
        }

        return haveRClone;
    }


    // ============================================================================
    // State Machine Implementation Methods
    // These methods contain the business logic for each state
    // ============================================================================

    // Retry state for _checkOnlineImpl
    private int _onlineCheckRetryCount = 0;
    private const int _maxOnlineCheckRetries = 10;
    private const int _baseRetryDelayMs = 1000;  // 1 second base
    private const int _retryDelayIncrementMs = 1000;  // +1 second per retry
    private const int _retryDelayJitterMs = 500;  // ±500ms randomization
    private static readonly Random _retryRandom = new();

    internal async Task _checkOnlineImpl()
    {
        _logger.LogInformation("RCloneService: Checking online.");
        
        try {
            using var scope = _serviceScopeFactory.CreateScope();
            var hannibalService = scope.ServiceProvider.GetRequiredService<IHannibalServiceClient>();
            var user = await hannibalService.GetUserAsync(-1, CancellationToken.None);

            if (null == user)
            {
                /*
                 * This means we cannot authenticate using username and password.
                 * This is an authentication error, not a transient network error.
                 */
                throw new UnauthorizedAccessException("No or invalid user login information.");
            }

            /*
             * We also need to read the list of storages to preload them before
             * we start rsync.
             */
            var storages = await hannibalService.GetStoragesAsync(CancellationToken.None);
            _listStorages = new List<Storage>(storages).AsReadOnly();
            
            /*
             * OK, no exception, online connection works. So progress.
             * Reset retry counter for next time.
             */
            _onlineCheckRetryCount = 0;
            await _stateMachine!.TransitionAsync(ServiceEvent.OnlineCheckPassed);

        }
        catch (UnauthorizedAccessException e)
        {
            /*
             * Authentication failed (401) - this won't fix itself by retrying.
             * Need new credentials, so go to WaitConfig.
             */
            _logger.LogError($"Authentication failed: {e.Message}");
            _areOptionsValid = false;
            _onlineCheckRetryCount = 0;
            await _stateMachine!.TransitionAsync(ServiceEvent.OnlineCheckFailed);
        }
        catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            /*
             * HTTP 401 Unauthorized - credentials are wrong, don't retry.
             */
            _logger.LogError($"HTTP 401 Unauthorized: {e.Message}");
            _areOptionsValid = false;
            _onlineCheckRetryCount = 0;
            await _stateMachine!.TransitionAsync(ServiceEvent.OnlineCheckFailed);
        }
        catch (Exception e)
        {
            /*
             * Other errors (network issues, server down, etc.) - may be transient.
             * Retry with linear backoff + jitter, up to max retries.
             */
            _onlineCheckRetryCount++;
            
            if (_onlineCheckRetryCount >= _maxOnlineCheckRetries)
            {
                /*
                 * Exceeded max retries, give up and wait for config change.
                 */
                _logger.LogError($"Failed to connect after {_maxOnlineCheckRetries} attempts: {e.Message}");
                _areOptionsValid = false;
                _onlineCheckRetryCount = 0;
                await _stateMachine!.TransitionAsync(ServiceEvent.OnlineCheckFailed);
                return;
            }
            
            /*
             * Calculate delay: linear backoff with randomization
             * Delay = base + (retryCount * increment) + random jitter
             */
            int jitter = _retryRandom.Next(-_retryDelayJitterMs, _retryDelayJitterMs + 1);
            int delayMs = _baseRetryDelayMs + (_onlineCheckRetryCount * _retryDelayIncrementMs) + jitter;
            delayMs = Math.Max(delayMs, 500);  // Minimum 500ms
            
            _logger.LogWarning(
                $"Could not connect to server (attempt {_onlineCheckRetryCount}/{_maxOnlineCheckRetries}): {e.Message}. " +
                $"Retrying in {delayMs}ms...");
            
            await Task.Delay(delayMs);
            await _checkOnlineImpl();
        }
    }

    internal async Task _backendsLoginImpl()
    {
        _logger.LogInformation("RCloneService: Backends logging in.");
        
        /*
         * We request backend login from our storages system, which in
         * turn will write the rclone config.
         */
        foreach (var storage in _listStorages)
        {
            _logger.LogInformation($"Creating storage state for ${storage.UriSchema}");
            
            /*
             * Ensure all storage states are created and initialized.
             */
            await _rcloneStorages.FindStorageState(storage, CancellationToken.None);
        }

        await _stateMachine!.TransitionAsync(ServiceEvent.BackendsLoggedIn);
    }

    internal async Task _checkRCloneProcessImpl()
    {
        _logger.LogInformation("RCloneService: Checking rclone process.");
        bool haveRCloneProcess = await _haveRCloneProcess(_defaultRCloneUrl);
        
        if (haveRCloneProcess)
        {
            await _stateMachine!.TransitionAsync(ServiceEvent.RCloneProcessFound);
        }
        else
        {
            await _stateMachine!.TransitionAsync(ServiceEvent.RCloneProcessNotFound);
        }
    }

    internal async Task _startRCloneProcessImpl()
    {
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
                haveRCloneProcess = await _haveRCloneProcess(_defaultRCloneUrl);
                if (haveRCloneProcess) break;
                _logger.LogWarning("RCloneService: waiting for rest interface to become available.");
                await Task.Delay(1000);
            }

            if (!haveRCloneProcess)
            {
                _areOptionsValid = false;
                _logger.LogError("RCloneService: rclone process did not start.");
                await _stateMachine!.TransitionAsync(ServiceEvent.RCloneProcessStartFailed);
                return;
            }   
        
            _logger.LogInformation("RCloneService: rclone process started.");
            await _stateMachine!.TransitionAsync(ServiceEvent.RCloneProcessStarted);
        }
        catch (Exception e)
        {
            _areOptionsValid = false;
            _logger.LogError($"Exception while starting rclone: {e}");
            await _stateMachine!.TransitionAsync(ServiceEvent.RCloneProcessStartFailed);
        }
    }

    internal async Task _handleWaitStartImpl()
    {
        _logger.LogInformation("RCloneService: WaitStart - checking for autostart.");
        
        /*
         * Must not happen, checked in previous state.
         */
        if (_options == null)
        {
            _logger.LogError("RCloneService: No options in WaitStart state!");
            return;
        }
        
        /*
         * Check if we should immediately transition to running
         */
        if (_stateMachine!.CanHandle(ServiceEvent.StopRequested))
        {
            // There's a queued stop request, let it be processed
            return;
        }
        
        if (_options.Autostart && !_wasUserStop)
        {
            _logger.LogInformation("RCloneService: Autostart enabled, transitioning to Running.");
            await _stateMachine.TransitionAsync(ServiceEvent.StartRequested);
        }
        else
        {
            _logger.LogInformation("RCloneService: Waiting for explicit start request.");
        }
    }

    internal async Task _startRunningImpl()
    {
        _logger.LogInformation("RCloneService: Running.");

        /*
        * Now we definitely transition to running.
        * So this is no user forced stop anymore. 
        */
        _wasUserStop = false;
        
        /*
         * Start the actual operation.
         * Unfortunately we cannot unsubscribe from this subscription, so we
         * need to check, if the connection is desired.
         */
        if (!_isConnectionSubscribed)
        {
            _hannibalConnection.On("NewJobAvailable", async () =>
            {
                if (_state.State == RCloneServiceState.ServiceState.Running)
                {
                    await _triggerFetchJobAsync(CancellationToken.None);
                }
            });
            
            _hannibalConnection.On("NewStorageOptionsAvailable", async () =>
            {
                if (_state.State == RCloneServiceState.ServiceState.Running)
                {
                    await _fetchStorageOptions(CancellationToken.None);
                }
            });
            
            // Listen for storage reauthentication events
            _hannibalConnection.On<string>("StorageReauthenticated", async (storageUriSchema) =>
            {
                _logger.LogInformation($"RCloneService: Received storage reauthentication event for {storageUriSchema}");
                
                try
                {
                    // Check if restart is actually needed
                    bool restartRequired = await _doesStorageChangeRequireRestart(storageUriSchema);
                    
                    if (!restartRequired)
                    {
                        _logger.LogInformation($"RCloneService: Storage {storageUriSchema} reauthenticated but no changes detected, continuing normally");
                        return;
                    }
                    
                    // Only trigger restart if actually needed
                    if (_stateMachine!.CanHandle(ServiceEvent.StorageReauthenticationRequired))
                    {
                        await _stateMachine.TransitionAsync(ServiceEvent.StorageReauthenticationRequired);
                    }
                    else
                    {
                        _logger.LogInformation($"RCloneService: Queueing reauth event (current state: {_state.State})");
                        _stateMachine.QueueEvent(ServiceEvent.StorageReauthenticationRequired);
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError($"Error checking storage reauth impact: {e}");
                    // On error, safer to restart
                    if (_stateMachine!.CanHandle(ServiceEvent.StorageReauthenticationRequired))
                    {
                        await _stateMachine.TransitionAsync(ServiceEvent.StorageReauthenticationRequired);
                    }
                }
            });
            
            _isConnectionSubscribed = true;
        }
    }

    internal async Task _stopJobsImpl()
    {
        _logger.LogInformation("RCloneService: Stopping jobs.");
        
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

        await _stateMachine!.TransitionAsync(ServiceEvent.JobsCompleted);
    }

    /// <summary>
    /// Handle storage reauthentication by cleaning up and preparing for restart
    /// </summary>
    internal async Task _handleStorageReauthImpl()
    {
        _logger.LogInformation("RCloneService: Handling storage reauthentication - cleaning up...");
        
        try
        {
            // 1. Stop all running jobs
            if (_rcloneHttpClient != null)
            {
                try
                {
                    var rcloneClient = new RCloneClient(_rcloneHttpClient);
                    var jobList = await rcloneClient.GetJobListAsync(CancellationToken.None);
                    
                    if (jobList.running_ids != null)
                    {
                        foreach (var jobid in jobList.running_ids)
                        {
                            _logger.LogInformation($"RCloneService: Stopping job {jobid} for reauth");
                            await rcloneClient.StopJobAsync(CancellationToken.None);
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger.LogWarning($"Failed to stop jobs gracefully: {e.Message}");
                }
            }
            
            // 2. Kill rclone process
            if (_processRClone != null && !_processRClone.HasExited)
            {
                _logger.LogInformation("RCloneService: Killing rclone process for reauth");
                _processRClone.Kill();
                _processRClone.Dispose();
                _processRClone = null;
            }
            
            // 3. Dispose HTTP client
            if (_rcloneHttpClient != null)
            {
                _rcloneHttpClient.Dispose();
                _rcloneHttpClient = null;
            }
            
            // 4. Clear job mappings
            lock (_lo)
            {
                _mapRCloneToJob.Clear();
            }
            
            // 5. Clear/reset config manager (will be regenerated with new tokens)
            if (_configManager != null)
            {
                _logger.LogInformation("RCloneService: Clearing rclone configuration");
                // Clear the config in memory and on disk
                _configManager = new RCloneConfigManager();
                _configManager.SaveToFile(_rcloneConfigFile());
            }
            
            // 6. Clear storage states in RCloneStorages so they reload with new tokens
            _rcloneStorages.ClearStorageStates();
            
            // 7. Reload the storage list from Hannibal to get updated tokens
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var hannibalService = scope.ServiceProvider.GetRequiredService<IHannibalServiceClient>();
                var storages = await hannibalService.GetStoragesAsync(CancellationToken.None);
                _listStorages = new List<Storage>(storages).AsReadOnly();
                _logger.LogInformation("RCloneService: Reloaded storage list with updated tokens");
            }
            catch (Exception e)
            {
                _logger.LogError($"Failed to reload storage list: {e}");
            }
            
            _logger.LogInformation("RCloneService: Cleanup complete, proceeding to backends login");
            
            // Transition to BackendsLoggingIn
            await _stateMachine!.TransitionAsync(ServiceEvent.ReauthCleanupComplete);
        }
        catch (Exception e)
        {
            _logger.LogError($"Error during reauth cleanup: {e}");
            // Still try to continue - transition to BackendsLoggingIn anyway
            await _stateMachine!.TransitionAsync(ServiceEvent.ReauthCleanupComplete);
        }
    }

    /// <summary>
    /// Check if storage change requires restart by comparing configurations
    /// </summary>
    private async Task<bool> _doesStorageChangeRequireRestart(string storageUriSchema)
    {
        _logger.LogInformation($"RCloneService: Checking if storage {storageUriSchema} requires restart");
        
        // 1. Get the storage object from our list
        Storage? storage = _listStorages?.FirstOrDefault(s => s.UriSchema == storageUriSchema);
        if (storage == null)
        {
            _logger.LogWarning($"Storage {storageUriSchema} not found in current list");
            // Unknown storage, safer to restart
            return true;
        }
        
        // 2. Get current parameters from config manager
        var currentParams = _configManager?.GetRemote(storageUriSchema);
        if (currentParams == null)
        {
            _logger.LogInformation($"Storage {storageUriSchema} not in current config, restart needed");
            return true;
        }
        
        // 3. Get new storage state with fresh tokens
        StorageState newState = await _rcloneStorages.FindStorageState(
            storage, CancellationToken.None, forceRefresh: true);
        
        // 4. Compare the parameters
        if (_areRCloneParametersEqual(currentParams, newState.RCloneParameters))
        {
            _logger.LogInformation($"Storage {storageUriSchema} parameters unchanged, no restart needed");
            return false;
        }
        
        _logger.LogInformation($"Storage {storageUriSchema} parameters changed, restart required");
        return true;
    }

    /// <summary>
    /// Compare two sets of rclone parameters for equality
    /// </summary>
    private bool _areRCloneParametersEqual(
        IDictionary<string, string> current, 
        IDictionary<string, string> updated)
    {
        // Check if all keys and values match
        if (current.Count != updated.Count)
            return false;
        
        foreach (var kvp in current)
        {
            if (!updated.TryGetValue(kvp.Key, out var updatedValue))
                return false;
                
            // Special handling for tokens - compare as case-sensitive
            if (kvp.Value != updatedValue)
                return false;
        }
        
        return true;
    }


    private async Task _checkConfig()
    {
        _logger.LogInformation("RCloneService: Checking configuration.");
        if (null == _options)
        {
            _logger.LogWarning("RCloneService: No configuration at all.");
            await _stateMachine!.TransitionAsync(ServiceEvent.ConfigInvalid);
            return;
        }

        if (!_areOptionsValid)
        {
            _logger.LogWarning("RCloneService: Invalidated configuration found.");
            await _stateMachine!.TransitionAsync(ServiceEvent.ConfigInvalid);
            return;
        }
        
        if (String.IsNullOrWhiteSpace(_options.BackerUsername)
            || String.IsNullOrWhiteSpace(_options.BackerPassword)
            || String.IsNullOrWhiteSpace(_options.RClonePath)
            || String.IsNullOrWhiteSpace(_options.RCloneOptions)
            || String.IsNullOrWhiteSpace(_options.UrlSignalR))
        {
            _logger.LogWarning("RCloneService: Configuration incomplete.");
            await _stateMachine!.TransitionAsync(ServiceEvent.ConfigInvalid);
            return;
        }
        
        /*
         * Configuration appears to be valid. Progress to the next step.
         */
        await _stateMachine!.TransitionAsync(ServiceEvent.ConfigReceived);
    }


    /**
     * If not running yet, start rclone job processing.
     */
    public async Task StartJobsAsync(CancellationToken cancellationToken)
    {
        if (_stateMachine == null)
        {
            _logger.LogWarning("StartJobsAsync called before service started.");
            return;
        }
        
        switch (_state.State)
        {
            case RCloneServiceState.ServiceState.Starting:
            case RCloneServiceState.ServiceState.WaitConfig:
            case RCloneServiceState.ServiceState.CheckOnline:
            case RCloneServiceState.ServiceState.BackendsLoggingIn:
            case RCloneServiceState.ServiceState.CheckRCloneProcess:
            case RCloneServiceState.ServiceState.StartRCloneProcess:
                /*
                 * Still booting up, queue the start request.
                 */
                _logger.LogInformation("RCloneService: Queueing start request (still starting up).");
                _stateMachine.QueueEvent(ServiceEvent.StartRequested);
                break;
            
            case RCloneServiceState.ServiceState.WaitStart:
                /*
                 * Ready to start, trigger immediately.
                 */
                _logger.LogInformation("RCloneService: Starting jobs.");
                await _stateMachine.TransitionAsync(ServiceEvent.StartRequested);
                break;
            
            case RCloneServiceState.ServiceState.Running:
                /*
                 * Already running, ignore request.
                 */
                _logger.LogInformation("RCloneService: Already running, ignoring start request.");
                break;

            case RCloneServiceState.ServiceState.WaitStop:
                /*
                 * Queue start request to restart after stopping.
                 */
                _logger.LogInformation("RCloneService: Queueing start request (currently stopping).");
                _stateMachine.QueueEvent(ServiceEvent.StartRequested);
                break;
            
            case RCloneServiceState.ServiceState.Exiting:
                /*
                 * Ignore, we are shutting down.
                 */
                _logger.LogWarning("RCloneService: Start requested but service is exiting.");
                break;
        }
    }


    /**
     * If running, stop rclone job processing.
     */
    public async Task StopJobsAsync(CancellationToken cancellationToken)
    {
        if (_stateMachine == null)
        {
            _logger.LogWarning("StopJobsAsync called before service started.");
            return;
        }
        
        _wasUserStop = true;
        
        switch (_state.State)
        {
            case RCloneServiceState.ServiceState.Starting:
            case RCloneServiceState.ServiceState.WaitConfig:
            case RCloneServiceState.ServiceState.CheckOnline:
            case RCloneServiceState.ServiceState.BackendsLoggingIn:
            case RCloneServiceState.ServiceState.CheckRCloneProcess:
            case RCloneServiceState.ServiceState.StartRCloneProcess:
                /*
                 * Still booting up, queue the stop request.
                 */
                _logger.LogInformation("RCloneService: Queueing stop request (still starting up).");
                _stateMachine.QueueEvent(ServiceEvent.StopRequested);
                break;
            
            case RCloneServiceState.ServiceState.WaitStart:
                /*
                 * Not started yet, nothing to stop.
                 */
                _logger.LogInformation("RCloneService: Not started yet, nothing to stop.");
                break;
            
            case RCloneServiceState.ServiceState.Running:
                /*
                 * Currently running, trigger stop.
                 */
                _logger.LogInformation("RCloneService: Stopping jobs.");
                await _stateMachine.TransitionAsync(ServiceEvent.StopRequested);
                break;

            case RCloneServiceState.ServiceState.WaitStop:
                /*
                 * Already stopping.
                 */
                _logger.LogInformation("RCloneService: Already stopping.");
                break;
            
            case RCloneServiceState.ServiceState.Exiting:
                /*
                 * Ignore, we are shutting down.
                 */
                _logger.LogWarning("RCloneService: Stop requested but service is exiting.");
                break;
        }
    }
    
    
    public async Task<TransferStatsResult> GetTransferStatsAsync(CancellationToken cancellationToken)
    {
        var rcloneClient = new RCloneClient(_rcloneHttpClient);

        var rcloneStats = await rcloneClient.GetJobStatsAsync(CancellationToken.None);
        _logger.LogInformation($"RCloneService: Transfer stats: {rcloneStats}");

        TransferStatsResult result = new();
        result.TransferringItems = new();
        if (rcloneStats.transferring != null)
        {
            foreach (var rcloneStatus in rcloneStats.transferring)
            {
                ItemTransferStatus stat = new()
                {
                    Speed = (float)rcloneStatus.speed,
                    AverageSpeed = (float)rcloneStatus.speedAvg,
                    BytesTransferred = rcloneStatus.bytes,
                    ETA = (rcloneStatus.eta!=null)?rcloneStatus.eta.Value!:0,
                    Name = (rcloneStatus.name!=null)?rcloneStatus.name:"",
                    PercentDone = (float)rcloneStatus.percentage,
                    TotalSize = rcloneStatus.size
                };
                result.TransferringItems.Add(stat);
            }
        }

        return result;
    }


    public async Task<SetStorageOptionsResult> SetStorageOptions(StorageOptions storageOptions, CancellationToken cancellationToken)
    {
        if (null == storageOptions)
        {
            throw new ArgumentNullException(nameof(storageOptions));
        }

        return new();
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


    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation($"StartAsync: Starting RCloneService with options {_options}");

        if (_isStarted)
        {
            throw new InvalidOperationException("Already started.");
        }
        _isStarted = true;

        await base.StartAsync(cancellationToken);

        _configManager = new();
        EnvironmentDetector.DirectoryExistsOrCreatable(_rcloneConfigDir());
        _configManager.LoadFromFile(_rcloneConfigFile());
        _configManager.SaveToFile(_rcloneConfigFile());
        
        /*
         * Initialize the state machine
         */
        _stateMachine = new RCloneStateMachine(this);
        
        /*
         * Initially, we wait for the configuration to arrive.
         */
        _state.SetState(RCloneServiceState.ServiceState.WaitConfig);
        await _checkConfig();
    }


    public RCloneServiceState GetState()
    {
        return new RCloneServiceState(_state);
    }
}
