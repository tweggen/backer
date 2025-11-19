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
    private enum ServiceState {
        /**
         * This instance just has started. Do nothing until we found where
         * we are.
         */
        Starting,
        
        /**
         * We found there is no valid configuration.
         * So wait until we received a valid configuration.
         * A configuration is valid, if it passes basic checks.
         * A configuration can be invalidated by a problem logging in
         * or a path that proves to be wrong.
         *
         * If the first validation passes, the state progresses to WaitConfig.
         */
        WaitConfig,
        
        /**
         * We appear to have a valid configuration. So try to log in
         * online by calling something.
         * If that goes wrong, we return to WaitConfig invalidating the
         * current configuration.
         */
        CheckOnline,
        
        /**
         * We check if there is a running rclone instance fitting our
         * requirements. If there is, we transition to Running.
         */
        CheckRCloneProcess,
        
        /**
         * Most probably, we did not have a running rclone instance.
         * So try to start one. If this does not work, mark the configuration
         * invalid and transition to WaitConfig.
         * Transition to Running otherwiese.
         */
        StartRCloneProcess,
        
        /**
         * We are checking for jobs, tryingf to execute them
         */
        Running,
        
        /**
         * Exit has been requested.
         */
        Exiting
    }

    private ServiceState _serviceState = ServiceState.Starting;
    
    private static object _classLock = new();
    private static int _nextId;

    private object _lo = new();
    
    private string _ownerId;
    private int _nRunningJobs = 0;
    
    private ILogger<RCloneService> _logger;
    private ProcessManager _processManager;
    private HubConnection _hannibalConnection;

    private RCloneServiceOptions? _options = null;
    private bool _areOptionsValid = true;

    private Process? _processRClone;
    private HttpClient? _rcloneHttpClient;

    private SortedDictionary<int, Job> _mapRCloneToJob = new();
    
    private readonly Channel<RCloneServiceParams> _taskChannel = Channel.CreateUnbounded<RCloneServiceParams>();
    
    private bool _isStarted = false;
    
    private readonly IServiceScopeFactory _serviceScopeFactory;

    private const string _defaultRCloneUrl = "http://localhost:5572";
    
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

            if (_serviceState == ServiceState.WaitConfig)
            {
                _options = updated;
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
                case ServiceState.Starting:
                case ServiceState.CheckOnline:
                case ServiceState.CheckRCloneProcess:
                case ServiceState.WaitConfig:
                case ServiceState.StartRCloneProcess:
                case ServiceState.Exiting:
                    /*
                     * No action required,
                     */
                    break;
                
                case ServiceState.Running:
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
            Job? job;
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var hannibalService = scope.ServiceProvider.GetRequiredService<IHannibalServiceClient>();
                job = await hannibalService.AcquireNextJobAsync(
                    new() { Username = "timo.weggen@gmail.com", Capabilities = "rclone", Owner = _ownerId },
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
            var result = await rcloneClient.GetPathsAsync(CancellationToken.None);
            haveRClone = true;
            _logger.LogInformation($"RCloneService: found working rclone instance with paths: {result}");
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


    private void _onNewState()
    {
        switch (_serviceState)
        {
            case ServiceState.Starting:
                _logger.LogInformation("RCloneService: Starting.");
                break;
            case ServiceState.WaitConfig:
                _logger.LogInformation("RCloneService: Waiting for configuration.");
                break;
            case ServiceState.CheckOnline:
                _logger.LogInformation("RCloneService: Checking online.");
                break;
            case ServiceState.CheckRCloneProcess:
                _logger.LogInformation("RCloneService: Checking rclone process.");
                break;
            case ServiceState.StartRCloneProcess:
                _logger.LogInformation("RCloneService: Starting rclone process.");
                break;
            case ServiceState.Running:
                _logger.LogInformation("RCloneService: Running.");
                break;
            case ServiceState.Exiting:
                _logger.LogInformation("RCloneService: Exiting.");
                break;
            
        }
    }


    private void _toWaitConfig()
    {
        _serviceState = ServiceState.WaitConfig;
        _logger.LogInformation("RCloneService: Waiting for configuration.");
        
        /*
         * We do not actively act, just wait for the REST put call.
         */
    }


    private async Task _toStartRClone()
    {
        _serviceState = ServiceState.StartRCloneProcess;
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
            await _toRunning();
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
        _serviceState = ServiceState.Running;
        _logger.LogInformation("RCloneService: Running.");
        
        /*
         * Start the actual operation.
         */
        _hannibalConnection.On("NewJobAvailable", async () =>
        {
            // TXWTODO: How to cancel this subscription?
            await _triggerFetchJob(CancellationToken.None);
        });
    }
    
    
    private async Task _toCheckRCloneProcess()
    {
        _serviceState = ServiceState.CheckRCloneProcess;
        _logger.LogInformation("RCloneService: Checking rclone process.");
        bool haveRCloneProcess = await _haveRCloneProcess(_defaultRCloneUrl);
        if (!haveRCloneProcess)
        {
            await _toStartRClone();
        }
        else
        {
            await _toRunning();
        }
    }
    

    private async Task _toCheckOnline()
    {
        _serviceState = ServiceState.CheckOnline;
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
        _serviceState = ServiceState.WaitConfig;
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
}
