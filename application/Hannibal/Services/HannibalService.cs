using System.Collections.Generic;
using System.Security.Claims;
using System.Security.Cryptography;
using Hannibal.Configuration;
using Hannibal.Data;
using Hannibal.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Endpoint = Hannibal.Models.Endpoint;

namespace Hannibal.Services;


public partial class HannibalService : IHannibalService
{
    private readonly HannibalContext _context;
    private readonly ILogger<HannibalService> _logger;
    private readonly HannibalServiceOptions _options;
    private readonly IHubContext<HannibalHub> _hannibalHub;
    private readonly IHttpContextAccessor _httpContextAccessor;
    
    private IdentityUser? _currentUser = null;
    
    private readonly UserManager<IdentityUser> _userManager;

    public HannibalService(
        HannibalContext context,
        ILogger<HannibalService> logger,
        IOptions<HannibalServiceOptions> options,
        IHubContext<HannibalHub> hannibalHub,
        UserManager<IdentityUser> userManager,
        IHttpContextAccessor httpContextAccessor,
        IBackgroundWorker backgroundWorker)
    {
        _context = context;
        _logger = logger;
        _options = options.Value;
        _hannibalHub = hannibalHub;
        _userManager = userManager;
        _httpContextAccessor = httpContextAccessor;
    }


    public Task<RunnerResult> StartRunnerAsync(CancellationToken cancellationToken)
    {
        
    }

    
    public Task<RunnerResult> StopRunnerAsync(CancellationToken cancellationToken)
    {
        
    }
    
    
    public async Task<IdentityUser> GetUserAsync(int id, CancellationToken cancellationToken)
    {
        var userClaims = _httpContextAccessor.HttpContext?.User;
        if (null != userClaims)
        {
            _currentUser = await _userManager.GetUserAsync(userClaims);
        }

        return _currentUser;
    }


    private async Task _obtainUser()
    {
        var userClaims = _httpContextAccessor.HttpContext?.User;
        if (null != userClaims)
        {
            _currentUser = await _userManager.GetUserAsync(userClaims);
        }
        else
        {
            throw new UnauthorizedAccessException("User not found");
        }
    }
    
    
    public async Task<CreateEndpointResult> CreateEndpointAsync(
        Endpoint endpoint,
        CancellationToken cancellationToken)
    {
        await _obtainUser();
        
        endpoint.UserId = _currentUser.Id;
        
        var storage = await _context.Storages.FirstAsync(s => s.Id == endpoint.StorageId, cancellationToken);
        if (null == storage)
        {
            throw new KeyNotFoundException($"No storage found for storageid {endpoint.StorageId}");
        }
        endpoint.Storage = storage;
        
        await _context.Endpoints.AddAsync(endpoint, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        return new CreateEndpointResult() { Id = endpoint.Id };
    }


    public async Task<Endpoint> GetEndpointAsync(
        string name,
        CancellationToken cancellationToken)
    {
        Endpoint? endpoint = await _context.Endpoints.FirstOrDefaultAsync(e => e.Name == name, cancellationToken);
        if (null == endpoint)
        {
            throw new KeyNotFoundException($"No endpoint found for name {name}");
        }

        return endpoint;
    }
    

    public async Task<IEnumerable<Endpoint>> GetEndpointsAsync(
        CancellationToken cancellationToken)
    {
        var listEndpoints = await _context.Endpoints
            .OrderBy(e => e.Name)
            .ToListAsync(cancellationToken);

        return listEndpoints;
    }


    public async Task<Storage> GetStorageAsync(
        int id,
        CancellationToken cancellationToken)
    {
        Storage? storage = await _context.Storages.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (null == storage)
        {
            throw new KeyNotFoundException($"No storage found for name {id}");
        }

        return storage;
    }
    
    
    public async Task<IEnumerable<Storage>> GetStoragesAsync(
        CancellationToken cancellationToken)
    {
        var listStorages = await _context.Storages.ToListAsync(cancellationToken);

        return listStorages;
    }
    

    public async Task DeleteEndpointAsync(
        int id,
        CancellationToken cancellationToken)
    {
        var endpoint = await _context.Endpoints.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (endpoint == null)
        {
            throw new KeyNotFoundException($"No endpoint found for id {id}");
        }

        _context.Endpoints.Remove(endpoint);
        await _context.SaveChangesAsync(cancellationToken);
    }

    
    public async Task<Endpoint> UpdateEndpointAsync(
        int id,
        Endpoint updatedEndpoint,
        CancellationToken cancellationToken)
    {
        var endpoint = await _context.Endpoints
            .Include(e => e.Storage)
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
            
        if (endpoint == null)
        {
            throw new KeyNotFoundException($"No endpoint found for id {id}");
        }

        // Verify the new user exists if it's being changed
        if (updatedEndpoint.UserId != endpoint.UserId)
        {
            throw new InvalidDataException($"Unable to change user id");
        }

        // Verify the new storage exists if it's being changed
        if (updatedEndpoint.StorageId != endpoint.StorageId)
        {
            var storage = await _context.Storages.FirstOrDefaultAsync(s => s.Id == updatedEndpoint.StorageId, cancellationToken);
            if (storage == null)
            {
                throw new KeyNotFoundException($"No storage found for storageid {updatedEndpoint.StorageId}");
            }
            endpoint.Storage = storage;
            endpoint.StorageId = updatedEndpoint.StorageId;
        }

        // Update other properties
        endpoint.Name = updatedEndpoint.Name;
        endpoint.Path = updatedEndpoint.Path;
        endpoint.Comment = updatedEndpoint.Comment;

        await _context.SaveChangesAsync(cancellationToken);
        return endpoint;
    }
    
    public async Task<CreateRuleResult> CreateRuleAsync(
        Rule rule,
        CancellationToken cancellationToken)
    {
        await _obtainUser();
        
        rule.UserId = _currentUser.Id;

        var sourceEndpoint = await _context.Endpoints.FirstAsync(e => e.Id == rule.SourceEndpointId, cancellationToken);
        if (null == sourceEndpoint)
        {
            throw new KeyNotFoundException($"No source endpoint found for endpointid {sourceEndpoint.Id}");
        }
        rule.SourceEndpoint = sourceEndpoint;
        rule.SourceEndpointId = sourceEndpoint.Id;
        
        var destinationEndpoint =
            await _context.Endpoints.FirstAsync(e => e.Id == rule.DestinationEndpointId, cancellationToken);
        if (null == destinationEndpoint)
        {
            throw new KeyNotFoundException($"No destination endpoint found for endpointid {destinationEndpoint.Id}");
        }
        rule.DestinationEndpoint = destinationEndpoint;
        rule.DestinationEndpointId = destinationEndpoint.Id;
            
        await _context.Rules.AddAsync(rule, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        return new CreateRuleResult() { Id = rule.Id };
    }
    

    public async Task<Rule> UpdateRuleAsync(
        int id,
        Rule updatedRule,
        CancellationToken cancellationToken)
    {
        await _obtainUser();
        
        var rule = await _context.Rules.FirstAsync(r => r.Id == id, cancellationToken);
                
        if (rule == null)
        {
            throw new KeyNotFoundException($"No rule found for id {id}");
        }

        rule.UserId = _currentUser.Id;

        var sourceEndpoint = await _context.Endpoints.FirstAsync(e => e.Id == rule.SourceEndpointId, cancellationToken);
        if (null == sourceEndpoint)
        {
            throw new KeyNotFoundException($"No source endpoint found for endpointid {sourceEndpoint.Id}");
        }
        rule.SourceEndpoint = sourceEndpoint;
        
        var destinationEndpoint =
            await _context.Endpoints.FirstAsync(e => e.Id == rule.DestinationEndpointId, cancellationToken);
        if (null == destinationEndpoint)
        {
            throw new KeyNotFoundException($"No destination endpoint found for endpointid {destinationEndpoint.Id}");
        }
        rule.DestinationEndpoint = destinationEndpoint;
            

        // Update other properties
        rule.Name = updatedRule.Name;
        rule.Comment = updatedRule.Comment;
        // We do not allow to change the username
        rule.SourceEndpoint = sourceEndpoint;
        rule.SourceEndpointId = sourceEndpoint.Id;
        rule.DestinationEndpoint = destinationEndpoint;
        rule.DestinationEndpointId = destinationEndpoint.Id;
        rule.Operation = updatedRule.Operation;
        rule.MaxDestinationAge = updatedRule.MaxDestinationAge;
        rule.MaxTimeAfterSourceModification = updatedRule.MaxTimeAfterSourceModification;
        rule.DailyTriggerTime = updatedRule.DailyTriggerTime;

        await _context.SaveChangesAsync(cancellationToken);

        /*
         * There might be a new job available right now.
         */
        await _hannibalHub.Clients.All.SendAsync("NewJobAvailable");

        return rule;
    }
    
    
    public async Task DeleteRuleAsync(
        int id,
        CancellationToken cancellationToken)
    {
        var rule = await _context.Rules.FirstAsync(e => e.Id == id, cancellationToken);
        if (rule == null)
        {
            throw new KeyNotFoundException($"No rule found for id {id}");
        }

        _context.Rules.Remove(rule);
        await _context.SaveChangesAsync(cancellationToken);
    }
    

    public async Task<Rule> GetRuleAsync(int ruleId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Information requested about rule {ruleId}", ruleId);

        var rule = await _context.Rules.FindAsync(ruleId);
        if (null == rule)
        {
            throw new KeyNotFoundException($"No rule found with id {ruleId}.");
        }

        return rule;
    }


    public async Task<IEnumerable<Rule>> GetRulesAsync(
        ResultPage resultPage, RuleFilter filter, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Rule list requested");

        var list = await _context.Rules.ToListAsync(cancellationToken);
        return list;
    }


    public async Task<Job> GetJobAsync(int jobId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Information requested about job {jobId}", jobId);

        var job = await _context.Jobs.FindAsync(jobId);
        if (null == job)
        {
            throw new KeyNotFoundException($"No job found with id {jobId}.");
        }

        return job;
    }


    public async Task<IEnumerable<Job>> GetJobsAsync(
        ResultPage resultPage, JobFilter filter, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Job list requested");

        var list = await _context.Jobs.ToListAsync(cancellationToken);
        return list;
    }


    private string _endpointKey(Endpoint endpoint) => $"{endpoint.StorageId}:{endpoint.Path}";
    

    /**
     * Acquire the next job to do.
     * We remember
     * - the owner of this job
     * - that the source endpoint of choice is reading
     * - that the target endpoint of choice is writing
     *
     * We take care that
     * - nobody is reading from the target endpoint or their parent
     * - nobody is writing the source endpoint or any endpoints within.
     */
    public async Task<Job> AcquireNextJobAsync(AcquireParams acquireParams, CancellationToken cancellationToken)
    {
        await _obtainUser();
        
        // var user = await _context.Users.FirstAsync(u => u.Username == acquireParams.Username, cancellationToken); 
        _logger.LogInformation("new job requested by for client with capas {capabilities}", acquireParams.Capabilities);

        var listPossibleJobs = await _context.Jobs
            .Where(j => j.State == Job.JobState.Ready && j.Owner == "")
            .Include(j => j.SourceEndpoint)
            .Include(j => j.DestinationEndpoint)
            .OrderBy(j => j.StartFrom)
            .ToListAsync(cancellationToken);

        Job? job = null;
        var mapStates = await _gatherEndpointAccess(_currentUser.Id);
        foreach (var candidate in listPossibleJobs)
        {
            if (_mayUseDestinationEndpoint(candidate.DestinationEndpoint, mapStates)
                && _mayUseSourceEndpoint(candidate.SourceEndpoint, mapStates))
            {
                job = candidate;
                break;
            }
        }
        
        if (job != null)
        {
            _logger.LogInformation("owner {owner} acquired job {jobId}.", acquireParams.Owner, job.Id);
            job.Owner = acquireParams.Owner;
            job.State = Job.JobState.Executing;
            await _context.SaveChangesAsync(cancellationToken);
            return job;
        }
        else
        {
            throw new KeyNotFoundException(
                $"No job found for owner {acquireParams.Owner} with caps {acquireParams.Capabilities}");
        }
    }


    /**
     * Look, how many jobs of one particular user access the given andpoint
     * either reading or writing.
     *
     * This is assuming any given user would not have an excessive number of jobs running.
     */
    public async Task<SortedDictionary<string, EndpointState.AccessState>> 
        _gatherEndpointAccess(string userId)
    {
        List<Job> listTimedOut = new();
        SortedDictionary<string, EndpointState.AccessState> mapStates = new();

        var now = DateTime.UtcNow;
        var timeout = TimeSpan.FromSeconds(120);

        var myOngoingJobs = await _context.Jobs
            .Where(j =>(j.UserId == userId)&& (j.State == Job.JobState.Executing))
            .Include(j => j.SourceEndpoint)
            .Include(j => j.DestinationEndpoint)
            .ToListAsync();
        foreach (var job in myOngoingJobs)
        {
            var age = now - job.LastReported;
            if (age > timeout)
            {
                _logger.LogInformation($"Timing out job {job.Tag}");
                listTimedOut.Add(job);
            }
            else
            {
                string jobSourceEndpointKey = _endpointKey(job.SourceEndpoint);
                if (mapStates.TryGetValue(jobSourceEndpointKey, out var sourceState))
                {
                    if (sourceState == EndpointState.AccessState.Writing)
                    {
                        _logger.LogWarning($"Warning: Endpoint {jobSourceEndpointKey} is in use for both reading and writing.");
                    }
                    else
                    {
                        /*
                         * In use for reading twice or idle.
                         */
                        mapStates[jobSourceEndpointKey] = EndpointState.AccessState.Reading;
                    }
                }
                else
                {
                    mapStates.Add(jobSourceEndpointKey, EndpointState.AccessState.Reading);
                }

                string jobDestinationEndpointKey = _endpointKey(job.DestinationEndpoint);
                if (mapStates.TryGetValue(jobDestinationEndpointKey, out var destState))
                {
                    if (destState != EndpointState.AccessState.Idle)
                    {
                        _logger.LogWarning($"Warning: Endpoint {jobDestinationEndpointKey} is in use for both {destState.ToString()}.");
                    }
                    else
                    {
                        /*
                         * That's ok.
                         */
                        mapStates[jobDestinationEndpointKey] = EndpointState.AccessState.Writing;
                    }
                }
                else
                {
                    mapStates.Add(jobDestinationEndpointKey, EndpointState.AccessState.Writing);
                }
            }
        }

        if (listTimedOut.Count > 0)
        {
            await _context.SaveChangesAsync();
        }

        return mapStates;
    }


    
    /**
     * Check, if the given endpoint may be used as a source.
     *
     * Any number of jobs may use the endpoint for reading.
     * However, it must not be in use for writing.
     */
    private bool _mayUseSourceEndpoint(
        Endpoint endpoint,
        SortedDictionary<string, EndpointState.AccessState> mapStates)
    {
        string endpointKey = _endpointKey(endpoint);
        foreach (var kvp in mapStates)
        {
            bool isWriting = kvp.Value == EndpointState.AccessState.Writing;
            if (isWriting)
            {
                bool isShared = endpointKey.StartsWith(kvp.Key) || kvp.Key.StartsWith(endpointKey);
                if (isShared)
                {
                    _logger.LogInformation($"Cannot use source endpoint {endpointKey} because it already is in use.");
                    return false;
                }
            }
        }

        return true;
    }
    
    
    /**
     * Check, if the given endpoint may be used as a destination.
     * Destination may only have one user.
     *
     * No other job must use it as a destination for writing ot
     * got reading.
     */
    private bool _mayUseDestinationEndpoint(
        Endpoint endpoint,
        SortedDictionary<string, EndpointState.AccessState> mapStates)
    {
        string endpointKey = _endpointKey(endpoint);
        foreach (var kvp in mapStates)
        {
            bool isShared = endpointKey.StartsWith(kvp.Key) || kvp.Key.StartsWith(endpointKey);
            if (isShared)
            {
                _logger.LogInformation($"Cannot use destination endpoint {endpointKey} because it already is in use.");
                return false;
            }
        }

        return true;
    }
    
    
    public async Task<Result> ReportJobAsync(JobStatus jobStatus, CancellationToken cancellationToken)
    {
        _logger.LogInformation("job {jobId} reported back status {jobStatus}", jobStatus.JobId, jobStatus.State);

        int result;
        bool hasFinished = false;
        
        var job = await _context.Jobs.FirstOrDefaultAsync(
            j => j.State == Job.JobState.Executing && j.Id == jobStatus.JobId, cancellationToken);
        if (job != null)
        {
            switch (jobStatus.State)
            {
                case Job.JobState.Executing:
                    job.LastReported = DateTime.UtcNow;
                    break;
                
                case Job.JobState.DoneFailure:
                    _logger.LogInformation("job {jobId} is not done", jobStatus.JobId);
                    /*
                     * Job failed. Can be executed once again. We do not remember the
                     * previous failure of the job.
                     */
                    // TXWTODO: Include something like number of retries? To not jam the pipeline with an erranous job?
                    job.State = Job.JobState.Ready;
                    job.Owner = "";
                    hasFinished = true;
                    break;
                
                case Job.JobState.DoneSuccess:
                    _logger.LogInformation("job {jobId} is done", jobStatus.JobId);
                    job.State = Job.JobState.DoneSuccess;
                    job.Owner = "";
                    hasFinished = true;
                    break;
            }
            
            await _context.SaveChangesAsync(cancellationToken);

            result = 0;
        }
        else
        {
            _logger.LogInformation("job {jobId} not found", jobStatus.JobId);
            
            /*
             * We consider it to be non-fatal to receive status reports for non-existing jobs.
             * This may happen due to restarts.
             * However, this is an error we reflect.
             */
            result = -1;
        }

        if (hasFinished)
        {
            /*
             * Inform all workers there might be a new job available right now.
             */
            await _hannibalHub.Clients.All.SendAsync("NewJobAvailable");
        }

        return new Result
        {
            Status = result
        };

    }


    public async Task<ShutdownResult> ShutdownAsync(CancellationToken cancellationTokens)
    {
        return new ShutdownResult() { ErrorCode = 0 };
    }
}

