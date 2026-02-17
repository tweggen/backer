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
using WorkerRClone.Services.Utils;

namespace WorkerRClone.Services;

public class RCloneService : BackgroundService
{
    internal RCloneServiceState _state = new();
    private RCloneStateMachine? _stateMachine;
    
    internal bool _wasUserStop = false;
    private bool _restartAfterReauth = false;
    
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

    private SortedDictionary<int, RunningJobInfo> _runningJobs = new();

    // Stderr error lines parsed from rclone output (includes file paths)
    private readonly object _stderrErrorsLock = new();
    private readonly List<string> _stderrErrors = new();

    // Count of consecutive token-related errors detected on rclone stderr.
    // Set by _readPrintLog, read and reset by the polling loop.
    private volatile int _stderrTokenErrorCount = 0;
    private const int _stderrTokenErrorThreshold = 3;

    // Timeout for stalled OAuth2 jobs with no activity (5 minutes)
    private static readonly TimeSpan _oauth2InactivityTimeout = TimeSpan.FromMinutes(5);

    // Rate limiting for mid-job token refresh restarts
    private DateTime _lastTokenRefreshRestart = DateTime.MinValue;
    private int _tokenRefreshRestartCount = 0;
    private const int _maxTokenRefreshRestarts = 3;
    private static readonly TimeSpan _tokenRefreshRestartCooldown = TimeSpan.FromMinutes(5);
    
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
    public Action<JobTransferStatsResult>? OnTransferStatsChanged { get; set; }

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
        if (_rcloneHttpClient == null)
        {
            return;  // Silent return - called frequently, would be noisy
        }

        // Fast path: if rclone stderr shows repeated token errors, trigger
        // reauth immediately instead of waiting for the inactivity timeout.
        if (_stderrTokenErrorCount >= _stderrTokenErrorThreshold)
        {
            _logger.LogWarning(
                "Detected {count} token-related errors on rclone stderr, triggering early token refresh.",
                _stderrTokenErrorCount);
            _stderrTokenErrorCount = 0;

            // Find the first running job with an OAuth2 endpoint to drive the refresh.
            RunningJobInfo? oauthJob = null;
            lock (_lo)
            {
                oauthJob = _runningJobs.Values.FirstOrDefault(r => r.HasOAuth2Endpoint);
            }

            if (oauthJob != null)
            {
                bool didRestart = await _tryMidJobTokenRefreshAsync(oauthJob, cancellationToken);
                if (didRestart) return;
            }
        }

        SortedDictionary<int, RunningJobInfo> mapJobs;
        /*
         * We need to create a list of jobs we already reported back to the caller
         * but that still are in rclone's queue.
         */
        List<int> listDoneJobs = new();
        lock (_lo)
        {
            mapJobs = new SortedDictionary<int, RunningJobInfo>(_runningJobs);
        }

        var rcloneClient = new RCloneClient(_rcloneHttpClient);

        // Get current transfer stats to check for activity
        JobStatsResult? currentStats = null;
        try
        {
            currentStats = await rcloneClient.GetJobStatsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug($"RCloneService: Unable to get job stats: {ex.Message}");
        }

        foreach (var kvp in mapJobs)
        {
            int rcloneJobId = kvp.Key;
            RunningJobInfo runningJobInfo = kvp.Value;
            Job job = runningJobInfo.Job;
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

                        // Reset token refresh restart count after successful job completion
                        _tokenRefreshRestartCount = 0;
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Unable to perform job {jobId} from {sourceEndpoint} to {destEndpoint} : {error}.",
                            jobId, job.SourceEndpoint.Path, job.DestinationEndpoint.Path, jobStatus.error);

                        /*
                         * Report back the error.
                         * If some transfers completed successfully, report DoneWithErrors
                         * instead of DoneFailure to avoid immediate retry loops.
                         */
                        var reportState = currentStats is { transfers: > 0 }
                            ? Job.JobState.DoneWithErrors
                            : Job.JobState.DoneFailure;

                        using var scope = _serviceScopeFactory.CreateScope();
                        var hannibalService = scope.ServiceProvider.GetRequiredService<IHannibalServiceClient>();
                        var reportRes = await hannibalService.ReportJobAsync(new()
                                { JobId = jobId, State = reportState, Owner = _ownerId },
                            cancellationToken);
                        listDoneJobs.Add(rcloneJobId);
                    }
                }
                else
                {
                    _logger.LogInformation($"Job Status for {jobId} is {jobStatus}.");

                    // Check for stalled OAuth2 job timeout
                    bool shouldTimeout = await _checkOAuth2InactivityTimeoutAsync(
                        runningJobInfo, currentStats, cancellationToken);

                    if (shouldTimeout)
                    {
                        // Attempt mid-job token refresh before giving up
                        bool didRestart = await _tryMidJobTokenRefreshAsync(runningJobInfo, cancellationToken);
                        if (didRestart)
                        {
                            // State machine is transitioning to RestartingForReauth,
                            // all jobs have been reported — stop checking
                            return;
                        }

                        // Token refresh not possible or failed — fall through to existing timeout behavior
                        _logger.LogWarning(
                            "Job {jobId} timed out: OAuth2 endpoint with expired token and no activity for {timeout}.",
                            jobId, _oauth2InactivityTimeout);

                        // Report job as failed due to OAuth2 timeout
                        using var scope = _serviceScopeFactory.CreateScope();
                        var hannibalService = scope.ServiceProvider.GetRequiredService<IHannibalServiceClient>();
                        await hannibalService.ReportJobAsync(new()
                                { JobId = jobId, State = Job.JobState.DoneFailure, Owner = _ownerId },
                            cancellationToken);
                        listDoneJobs.Add(rcloneJobId);

                        // Try to stop the rclone job
                        try
                        {
                            await rcloneClient.StopJobAsync(rcloneJobId, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug($"Failed to stop timed-out job {rcloneJobId}: {ex.Message}");
                        }
                    }
                    else
                    {
                        // TXWTODO: Throttle the amount of reportjobasync calls.
                        using var scope = _serviceScopeFactory.CreateScope();
                        var hannibalService = scope.ServiceProvider.GetRequiredService<IHannibalServiceClient>();
                        var reportRes = await hannibalService.ReportJobAsync(new()
                                { JobId = jobId, State = Job.JobState.Executing, Owner = _ownerId },
                            cancellationToken);
                    }
                }
            }
            catch (HttpRequestException)
            {
                // RClone unavailable - break out, no point trying other jobs
                _logger.LogDebug("RCloneService: Unable to check jobs - rclone not available");
                break;
            }
            catch (TaskCanceledException ex) when (ex.CancellationToken != cancellationToken)
            {
                // Timeout or connection failure (not user cancellation)
                _logger.LogDebug("RCloneService: Unable to check jobs - request timed out");
                break;
            }
            catch (Exception)
            {
                // Job-specific error (e.g., job ceased to exist) - continue to next job
            }
        }

        lock (_lo)
        {
            foreach (int deadRcloneJobId in listDoneJobs)
            {
                _runningJobs.Remove(deadRcloneJobId);
            }
        }
    }

    /// <summary>
    /// Check if a job should be timed out due to OAuth2 inactivity.
    /// Timeout conditions (all must be true):
    /// a) At least one endpoint uses OAuth2 and its token is expired
    /// b) No active transmission (no bytes being transferred)
    /// c) No activity for 5 minutes
    /// </summary>
    private async Task<bool> _checkOAuth2InactivityTimeoutAsync(
        RunningJobInfo runningJobInfo,
        JobStatsResult? currentStats,
        CancellationToken cancellationToken)
    {
        var job = runningJobInfo.Job;

        // Condition a) Check if job has OAuth2 endpoint with expired token
        if (!runningJobInfo.HasOAuth2Endpoint)
        {
            return false;  // No OAuth2 endpoints, no timeout needed
        }

        bool hasExpiredToken = false;
        if (runningJobInfo.SourceIsOAuth2 && _isOAuth2TokenExpired(job.SourceEndpoint.Storage))
        {
            hasExpiredToken = true;
            _logger.LogDebug($"Job {job.Id}: Source OAuth2 token is expired");
        }
        if (runningJobInfo.DestinationIsOAuth2 && _isOAuth2TokenExpired(job.DestinationEndpoint.Storage))
        {
            hasExpiredToken = true;
            _logger.LogDebug($"Job {job.Id}: Destination OAuth2 token is expired");
        }

        if (!hasExpiredToken)
        {
            // Tokens are still valid, reset activity timer
            runningJobInfo.LastActivityAt = DateTime.UtcNow;
            return false;
        }

        // Condition b) Check for active transmission
        long currentBytes = 0;
        if (currentStats != null)
        {
            currentBytes = currentStats.bytes;
        }

        if (currentBytes > runningJobInfo.LastBytesTransferred)
        {
            // Bytes are being transferred, update activity
            _logger.LogDebug($"Job {job.Id}: Activity detected, bytes transferred: {runningJobInfo.LastBytesTransferred} -> {currentBytes}");
            runningJobInfo.LastBytesTransferred = currentBytes;
            runningJobInfo.LastActivityAt = DateTime.UtcNow;
            return false;
        }

        // Condition c) Check if inactive for 5 minutes
        var inactiveDuration = DateTime.UtcNow - runningJobInfo.LastActivityAt;
        if (inactiveDuration < _oauth2InactivityTimeout)
        {
            _logger.LogDebug($"Job {job.Id}: Inactive for {inactiveDuration.TotalSeconds:F0}s (timeout at {_oauth2InactivityTimeout.TotalSeconds}s)");
            return false;
        }

        // All conditions met - job should be timed out
        _logger.LogInformation(
            $"Job {job.Id}: OAuth2 inactivity timeout triggered. " +
            $"Expired token: true, No activity for: {inactiveDuration.TotalMinutes:F1} minutes");
        return true;
    }


    /// <summary>
    /// Attempt to refresh expired OAuth2 tokens mid-job and trigger a restart.
    /// Rate-limited to prevent restart loops.
    /// Returns true if a restart was triggered (caller should return immediately).
    /// </summary>
    private async Task<bool> _tryMidJobTokenRefreshAsync(
        RunningJobInfo runningJobInfo,
        CancellationToken cancellationToken)
    {
        var job = runningJobInfo.Job;

        // Rate limiting: not within cooldown of last restart
        var timeSinceLastRestart = DateTime.UtcNow - _lastTokenRefreshRestart;
        if (timeSinceLastRestart < _tokenRefreshRestartCooldown)
        {
            _logger.LogDebug(
                "Job {jobId}: Token refresh restart rate-limited (last restart {seconds}s ago, cooldown {cooldown}s)",
                job.Id, timeSinceLastRestart.TotalSeconds, _tokenRefreshRestartCooldown.TotalSeconds);
            return false;
        }

        // Rate limiting: consecutive restart count
        if (_tokenRefreshRestartCount >= _maxTokenRefreshRestarts)
        {
            _logger.LogWarning(
                "Job {jobId}: Token refresh restart limit reached ({count}/{max} consecutive restarts)",
                job.Id, _tokenRefreshRestartCount, _maxTokenRefreshRestarts);
            return false;
        }

        // Identify expired endpoint(s) and try to refresh tokens
        bool anyRefreshed = false;

        if (runningJobInfo.SourceIsOAuth2 && _isOAuth2TokenExpired(job.SourceEndpoint.Storage))
        {
            try
            {
                var result = await _rcloneStorages.EnsureTokensValidAsync(
                    job.SourceEndpoint.Storage, cancellationToken: cancellationToken);
                if (result.IsNowValid)
                {
                    _logger.LogInformation("Job {jobId}: Source OAuth2 token refreshed for {storage}",
                        job.Id, job.SourceEndpoint.Storage.UriSchema);
                    anyRefreshed = true;
                }
                else
                {
                    _logger.LogWarning("Job {jobId}: Source token refresh failed: {error}",
                        job.Id, result.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Job {jobId}: Source token refresh threw: {error}", job.Id, ex.Message);
            }
        }

        if (runningJobInfo.DestinationIsOAuth2 && _isOAuth2TokenExpired(job.DestinationEndpoint.Storage))
        {
            try
            {
                var result = await _rcloneStorages.EnsureTokensValidAsync(
                    job.DestinationEndpoint.Storage, cancellationToken: cancellationToken);
                if (result.IsNowValid)
                {
                    _logger.LogInformation("Job {jobId}: Destination OAuth2 token refreshed for {storage}",
                        job.Id, job.DestinationEndpoint.Storage.UriSchema);
                    anyRefreshed = true;
                }
                else
                {
                    _logger.LogWarning("Job {jobId}: Destination token refresh failed: {error}",
                        job.Id, result.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Job {jobId}: Destination token refresh threw: {error}", job.Id, ex.Message);
            }
        }

        if (!anyRefreshed)
        {
            _logger.LogInformation("Job {jobId}: No tokens could be refreshed, falling through to timeout", job.Id);
            return false;
        }

        // Token refresh succeeded — report ALL running jobs as DoneFailure before restart
        _logger.LogInformation("Token refresh succeeded, reporting all running jobs and triggering restart");

        SortedDictionary<int, RunningJobInfo> snapshot;
        lock (_lo)
        {
            snapshot = new SortedDictionary<int, RunningJobInfo>(_runningJobs);
        }

        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var hannibalService = scope.ServiceProvider.GetRequiredService<IHannibalServiceClient>();
            foreach (var kvp in snapshot)
            {
                try
                {
                    await hannibalService.ReportJobAsync(new()
                        { JobId = kvp.Value.Job.Id, State = Job.JobState.DoneFailure, Owner = _ownerId },
                        cancellationToken);
                    _logger.LogInformation("Reported job {jobId} as DoneFailure for token refresh restart",
                        kvp.Value.Job.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed to report job {jobId} during token refresh restart: {error}",
                        kvp.Value.Job.Id, ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to create scope for reporting jobs during token refresh: {error}", ex.Message);
        }

        // Update rate-limit fields
        _lastTokenRefreshRestart = DateTime.UtcNow;
        _tokenRefreshRestartCount++;

        // Trigger state machine transition to restart rclone with new tokens
        if (_stateMachine!.CanHandle(ServiceEvent.StorageReauthenticationRequired))
        {
            await _stateMachine.TransitionAsync(ServiceEvent.StorageReauthenticationRequired);
        }
        else
        {
            _logger.LogInformation("Queueing StorageReauthenticationRequired (current state: {state})", _state.State);
            _stateMachine.QueueEvent(ServiceEvent.StorageReauthenticationRequired);
        }

        return true;
    }

    /**
     * Take care we have all remotes that we need for this endpoint in the configuration.
     * If necessary, create them, restart rclone.
     * Also ensures tokens are valid before starting the job.
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
        
        // Ensure tokens are valid before starting the job
        var tokenResult = await _rcloneStorages.EnsureTokensValidAsync(storage, cancellationToken: cancellationToken);
        if (!tokenResult.IsNowValid)
        {
            _logger.LogError($"Token validation failed for storage {storage.UriSchema}: {tokenResult.ErrorMessage}");
            throw new UnauthorizedAccessException($"Token validation failed for storage {storage.UriSchema}: {tokenResult.ErrorMessage}");
        }
        
        if (tokenResult.WasRefreshed)
        {
            _logger.LogInformation($"Tokens were refreshed for storage {storage.UriSchema}");
        }
        
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
            // Check if endpoints use OAuth2
            bool sourceIsOAuth2 = await _isOAuth2StorageAsync(job.SourceEndpoint.Storage);
            bool destIsOAuth2 = await _isOAuth2StorageAsync(job.DestinationEndpoint.Storage);

            lock (_lo)
            {
                _runningJobs[asyncResult.jobid] = new RunningJobInfo
                {
                    Job = job,
                    RCloneJobId = asyncResult.jobid,
                    SourceIsOAuth2 = sourceIsOAuth2,
                    DestinationIsOAuth2 = destIsOAuth2
                };
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

            // Parse ERROR lines to capture file paths for error reporting.
            // rclone stderr format: "ERROR : <filepath>: <error message>"
            // The API's lastError only contains the error message without the filepath,
            // so we capture the full line here to enrich error reports.
            const string errorPrefix = "ERROR : ";
            int errorIdx = message.IndexOf(errorPrefix);
            if (errorIdx >= 0)
            {
                string errorDetail = message[(errorIdx + errorPrefix.Length)..];
                lock (_stderrErrorsLock)
                {
                    _stderrErrors.Add(errorDetail);
                    while (_stderrErrors.Count > 200)
                    {
                        _stderrErrors.RemoveAt(0);
                    }
                }

                // Detect token expiry errors early so the polling loop can trigger
                // a reauth restart without waiting for the full inactivity timeout.
                if (errorDetail.Contains("couldn't fetch token")
                    || errorDetail.Contains("maybe token expired"))
                {
                    Interlocked.Increment(ref _stderrTokenErrorCount);
                }
            }
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

        if (null == _configManager)
        {
            _logger.LogWarning("RCloneService._backendsLoginImpl(): Warning: _configManager == null.");
        }
        
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
            var ss = await _rcloneStorages.FindStorageState(storage, CancellationToken.None);
            if (_configManager != null)
            {
                _configManager.AddOrUpdateRemote(storage.UriSchema, ss.RCloneParameters);
            }

        }

        if (_configManager != null)
        {
            _logger.LogInformation($"Saving rclone config to {_rcloneConfigFile()}");
            _configManager.SaveToFile(_rcloneConfigFile());
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
        
        if (_restartAfterReauth)
        {
            _restartAfterReauth = false;
            _logger.LogInformation("RCloneService: Restarting after reauthentication, transitioning to Running.");
            await _stateMachine.TransitionAsync(ServiceEvent.StartRequested);
        }
        else if (_options.Autostart && !_wasUserStop)
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

        if (_rcloneHttpClient == null)
        {
            _logger.LogInformation("RCloneService: RClone not available, skipping job stop.");
            await _stateMachine!.TransitionAsync(ServiceEvent.JobsCompleted);
            return;
        }

        try
        {
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
                    await rcloneClient.StopJobAsync(jobid, CancellationToken.None);
                }
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("RCloneService: Unable to stop jobs - rclone not available: {Message}", ex.Message);
        }
        catch (TaskCanceledException ex) when (!ex.CancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("RCloneService: Unable to stop jobs - request timed out");
        }

        await _stateMachine!.TransitionAsync(ServiceEvent.JobsCompleted);
    }

    /// <summary>
    /// Handle storage reauthentication by cleaning up and preparing for restart
    /// </summary>
    internal async Task _handleStorageReauthImpl()
    {
        _logger.LogInformation("RCloneService: Handling storage reauthentication - cleaning up...");

        // The service was running when the token expired, so ensure we
        // auto-restart after reauth rather than getting stuck in WaitStart.
        _wasUserStop = false;
        _restartAfterReauth = true;
        _stderrTokenErrorCount = 0;

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
                            await rcloneClient.StopJobAsync(jobid, CancellationToken.None);
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
            
            // 4. Report interrupted jobs to Hannibal before clearing
            {
                List<Job> jobsToReport;
                lock (_lo)
                {
                    jobsToReport = _runningJobs.Values.Select(r => r.Job).ToList();
                }

                if (jobsToReport.Count > 0)
                {
                    _logger.LogInformation("Reporting {count} interrupted job(s) as DoneFailure for reauth restart",
                        jobsToReport.Count);
                    try
                    {
                        using var scope = _serviceScopeFactory.CreateScope();
                        var hannibalService = scope.ServiceProvider.GetRequiredService<IHannibalServiceClient>();
                        foreach (var job in jobsToReport)
                        {
                            try
                            {
                                await hannibalService.ReportJobAsync(
                                    new() { JobId = job.Id, State = Job.JobState.DoneFailure, Owner = _ownerId },
                                    CancellationToken.None);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning("Failed to report job {jobId} during reauth: {error}",
                                    job.Id, ex.Message);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Failed to create scope for reporting jobs during reauth: {error}",
                            ex.Message);
                    }
                }
            }

            // 5. Clear job mappings
            lock (_lo)
            {
                _runningJobs.Clear();
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

        // 1. Get current parameters from config manager (what rclone is using now)
        var currentParams = _configManager?.GetRemote(storageUriSchema);
        if (currentParams == null)
        {
            _logger.LogInformation($"Storage {storageUriSchema} not in current config, restart needed");
            return true;
        }

        // 2. Fetch the updated storage from the database (with new tokens from the server)
        // This is important: we fetch from DB, NOT refresh OAuth tokens again
        Storage? updatedStorage;
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var hannibalService = scope.ServiceProvider.GetRequiredService<IHannibalServiceClient>();
            var storages = await hannibalService.GetStoragesAsync(CancellationToken.None);
            updatedStorage = storages.FirstOrDefault(s => s.UriSchema == storageUriSchema);
        }
        catch (Exception e)
        {
            _logger.LogWarning($"Failed to fetch updated storage {storageUriSchema}: {e.Message}");
            // Can't check, safer to restart
            return true;
        }

        if (updatedStorage == null)
        {
            _logger.LogWarning($"Storage {storageUriSchema} not found in database");
            return true;
        }

        // 3. Get storage state WITHOUT refreshing OAuth tokens (forceRefresh: false)
        // This builds the rclone parameters from the storage's current tokens in the database
        // Using forceRefresh: true would trigger another OAuth refresh cycle causing an infinite loop
        StorageState newState = await _rcloneStorages.FindStorageState(
            updatedStorage, CancellationToken.None, forceRefresh: false);

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

    /// <summary>
    /// Check if a storage uses OAuth2 authentication
    /// </summary>
    private async Task<bool> _isOAuth2StorageAsync(Storage storage)
    {
        try
        {
            if (!_rcloneStorages.IsSupported(storage.Technology))
            {
                return false;
            }

            var state = await _rcloneStorages.FindStorageState(storage, CancellationToken.None);
            // A storage uses OAuth2 if it has an OAuthClient set up
            return state.OAuthClient != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Check if OAuth2 tokens are expired for a storage
    /// </summary>
    private bool _isOAuth2TokenExpired(Storage storage)
    {
        // If no expiry set or no refresh token, can't determine expiry
        if (string.IsNullOrEmpty(storage.RefreshToken))
        {
            return false;
        }

        // Check if token has expired (no buffer - we want to know if it's actually expired)
        var now = DateTime.UtcNow;
        var expiresAt = storage.ExpiresAt.ToUniversalTime();
        return now >= expiresAt;
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
    
    
    public async Task<JobTransferStatsResult> GetJobTransferStatsAsync(CancellationToken cancellationToken)
    {
        if (_rcloneHttpClient == null)
        {
            return new JobTransferStatsResult
            {
                Jobs = new(),
                OverallStats = new OverallTransferStats()
            };
        }

        var rcloneClient = new RCloneClient(_rcloneHttpClient);

        // 1. Get global stats for overall progress
        JobStatsResult globalStats;
        try
        {
            globalStats = await rcloneClient.GetJobStatsAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("RCloneService: Unable to get transfer stats - rclone not available: {Message}", ex.Message);
            return new JobTransferStatsResult
            {
                Jobs = new(),
                OverallStats = new OverallTransferStats()
            };
        }
        catch (TaskCanceledException ex) when (ex.CancellationToken != cancellationToken)
        {
            _logger.LogWarning("RCloneService: Unable to get transfer stats - request timed out or connection failed");
            return new JobTransferStatsResult
            {
                Jobs = new(),
                OverallStats = new OverallTransferStats()
            };
        }

        var result = new JobTransferStatsResult();

        // 2. Build overall stats
        result.OverallStats = new OverallTransferStats
        {
            BytesTransferred = globalStats.bytes,
            TotalBytes = globalStats.totalBytes,
            Speed = globalStats.speed,
            EtaSeconds = globalStats.eta,
            ElapsedSeconds = globalStats.elapsedTime,
            FilesCompleted = globalStats.transfers,
            TotalFiles = globalStats.totalTransfers,
            Errors = globalStats.errors
        };

        // 3. Snapshot running jobs
        SortedDictionary<int, RunningJobInfo> snapshot;
        lock (_lo)
        {
            snapshot = new SortedDictionary<int, RunningJobInfo>(_runningJobs);
        }

        // 4. For each running job, get per-job stats
        foreach (var (rcloneJobId, runningJob) in snapshot)
        {
            var jobInfo = new JobTransferInfo
            {
                HannibalJobId = runningJob.Job.Id,
                RCloneJobId = rcloneJobId,
                Tag = runningJob.Job.Tag ?? "",
                SourcePath = runningJob.Job.SourceEndpoint?.Path ?? "",
                DestinationPath = runningJob.Job.DestinationEndpoint?.Path ?? "",
                StartedAt = runningJob.StartedAt,
                LastTransferActivity = runningJob.LastTransferActivity
            };

            try
            {
                var perJobStats = await rcloneClient.GetJobStatsAsync($"job/{rcloneJobId}", cancellationToken);

                // Error tracking: enrich with file path from stderr if available
                if (!string.IsNullOrEmpty(perJobStats.lastError) && perJobStats.lastError != runningJob.LastSeenError)
                {
                    runningJob.LastSeenError = perJobStats.lastError;
                    if (runningJob.Errors.Count < 10)
                    {
                        // Try to find a matching stderr error that includes the file path.
                        // The API's lastError lacks the filepath, but stderr has it.
                        string errorToAdd = perJobStats.lastError;
                        lock (_stderrErrorsLock)
                        {
                            var match = _stderrErrors.FirstOrDefault(e => e.Contains(perJobStats.lastError));
                            if (match != null)
                            {
                                errorToAdd = match;
                                _stderrErrors.Remove(match);
                            }
                        }
                        runningJob.Errors.Add(errorToAdd);
                    }
                }

                // Transfer activity tracking
                if (perJobStats.transferring != null && perJobStats.transferring.Count > 0)
                {
                    runningJob.LastTransferActivity = DateTime.UtcNow;
                    jobInfo.LastTransferActivity = runningJob.LastTransferActivity;
                }

                // Map transferring items
                if (perJobStats.transferring != null)
                {
                    foreach (var t in perJobStats.transferring)
                    {
                        jobInfo.Transfers.Add(new ItemTransferStatus
                        {
                            Speed = (float)t.speed,
                            AverageSpeed = (float)t.speedAvg,
                            BytesTransferred = t.bytes,
                            ETA = t.eta ?? 0,
                            Name = t.name ?? "",
                            PercentDone = (float)t.percentage,
                            TotalSize = t.size
                        });
                    }
                }

                // Per-job stats
                jobInfo.Stats = new OverallTransferStats
                {
                    BytesTransferred = perJobStats.bytes,
                    TotalBytes = perJobStats.totalBytes,
                    Speed = perJobStats.speed,
                    EtaSeconds = perJobStats.eta,
                    ElapsedSeconds = perJobStats.elapsedTime,
                    FilesCompleted = perJobStats.transfers,
                    TotalFiles = perJobStats.totalTransfers,
                    Errors = perJobStats.errors
                };

                jobInfo.ErrorCount = perJobStats.errors;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Failed to get per-job stats for rclone job {JobId}: {Message}", rcloneJobId, ex.Message);
            }

            jobInfo.Errors = runningJob.Errors.ToList();
            result.Jobs.Add(jobInfo);
        }

        return result;
    }


    public async Task AbortJobAsync(int hannibalJobId, CancellationToken cancellationToken)
    {
        int rcloneJobId;
        lock (_lo)
        {
            var entry = _runningJobs.FirstOrDefault(kvp => kvp.Value.Job.Id == hannibalJobId);
            if (entry.Value == null)
            {
                _logger.LogInformation("AbortJobAsync: job {JobId} not found (already finished?)", hannibalJobId);
                return;
            }
            rcloneJobId = entry.Key;
        }

        // Stop the rclone job
        if (_rcloneHttpClient != null)
        {
            try
            {
                var rcloneClient = new RCloneClient(_rcloneHttpClient);
                await rcloneClient.StopJobAsync(rcloneJobId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to stop rclone job {RCloneJobId}: {Message}", rcloneJobId, ex.Message);
            }
        }

        // Report as cancelled to Hannibal
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var hannibalService = scope.ServiceProvider.GetRequiredService<IHannibalServiceClient>();
            await hannibalService.ReportJobAsync(
                new() { JobId = hannibalJobId, State = Job.JobState.Cancelled, Owner = _ownerId },
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to report job {JobId} as cancelled: {Message}", hannibalJobId, ex.Message);
        }

        // Remove from running jobs
        lock (_lo)
        {
            _runningJobs.Remove(rcloneJobId);
        }
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

        /*
         * Report all acquired jobs as failed so they can be retried.
         */
        List<Job> jobsToReport;
        lock (_lo)
        {
            jobsToReport = _runningJobs.Values.Select(r => r.Job).ToList();
            _runningJobs.Clear();
        }

        if (jobsToReport.Count > 0)
        {
            _logger.LogInformation($"Reporting {jobsToReport.Count} acquired job(s) as failed due to shutdown.");
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var hannibalService = scope.ServiceProvider.GetRequiredService<IHannibalServiceClient>();
                foreach (var job in jobsToReport)
                {
                    try
                    {
                        _logger.LogInformation($"Reporting job {job.Id} as failed due to shutdown.");
                        await hannibalService.ReportJobAsync(
                            new() { JobId = job.Id, State = Job.JobState.DoneFailure, Owner = _ownerId },
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to report job {job.Id} during shutdown.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create scope for reporting jobs during shutdown.");
            }
        }

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
