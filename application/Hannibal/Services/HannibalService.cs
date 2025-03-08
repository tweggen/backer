using System.Collections.Generic;
using System.Security.Cryptography;
using Hannibal.Configuration;
using Hannibal.Data;
using Hannibal.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hannibal.Services;


public class HannibalService : IHannibalService
{
    private object _lo = new();

    private readonly HannibalContext _context;
    private readonly ILogger<HannibalService> _logger;
    private readonly HannibalServiceOptions _options;
    private readonly IHubContext<HannibalHub> _hannibalHub;

    /**
     * Until we have a real database backend, we fake new entries using _nextId.
     */
    private static int _nextId;

    public HannibalService(
        HannibalContext context,
        ILogger<HannibalService> logger,
        IOptions<HannibalServiceOptions> options,
        IHubContext<HannibalHub> hannibalHub)
    {
        _context = context;
        _logger = logger;
        _options = options.Value;
        _hannibalHub = hannibalHub;
    }

    
    public async Task<CreateRuleResult> CreateRuleAsync(
        Rule rule,
        CancellationToken cancellationToken)
    {
        await _context.Rules.AddAsync(rule, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        return new CreateRuleResult() { Id = rule.Id };
    }
    

    public async Task<Rule> UpdateRuleAsync(
        int id,
        Rule updatedRule,
        CancellationToken cancellationToken)
    {
        var rule = await _context.Rules.FirstAsync(r => r.Id == id, cancellationToken);
                
        if (rule == null)
        {
            throw new KeyNotFoundException($"No rule found for id {id}");
        }

        // Update other properties
        rule.Name = updatedRule.Name;
        rule.Comment = updatedRule.Comment;
        // We do not allow to change the username
        rule.SourceEndpoint = updatedRule.SourceEndpoint;
        rule.DestinationEndpoint = updatedRule.DestinationEndpoint;
        rule.Operation = updatedRule.Operation;
        rule.MaxDestinationAge = updatedRule.MaxDestinationAge;
        rule.MaxTimeAfterSourceModification = updatedRule.MaxTimeAfterSourceModification;
        rule.DailyTriggerTime = updatedRule.DailyTriggerTime;

        await _context.SaveChangesAsync(cancellationToken);
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


    /**
     * Acquire the next job to do.
     * We remember
     * - the owner of this job
     * - that the source endpoint of choice is reading
     * - that the target endpoint of choice is writing
     *
     * We take care that
     * - nobody is reading from the target endpoint or their parent
     * - nobody is writing the the source endpoint or any endpoints within.
     */
    public async Task<Job> AcquireNextJobAsync(AcquireParams acquireParams, CancellationToken cancellationToken)
    {
        _logger.LogInformation("new job requested by for client with capas {capabilities}", acquireParams.Capabilities);

        var listPossibleJobs = await _context.Jobs
            .Where(j => 
                j.State == Job.JobState.Ready 
                && j.Owner == "")
            .OrderBy(j => j.StartFrom)
            .ToListAsync(cancellationToken);

        Job? job = null;
        var mapStates = await _gatherEndpointAccess(acquireParams.Username);
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
        _gatherEndpointAccess(string username)
    {
        List<Job> listTimedOut = new();
        SortedDictionary<string, EndpointState.AccessState> mapStates = new();

        var now = DateTime.UtcNow;
        var timeout = TimeSpan.FromSeconds(120);

        var myOngoingJobs = await _context.Jobs.Where(j => (j.Username == username) && (j.State == Job.JobState.Executing)).ToListAsync();
        foreach (var job in myOngoingJobs)
        {
            var age = now - job.LastReported;
            if (age > timeout)
            {
                // TXWTODO: THis is a race, however, it would have timed out a minute later.
                listTimedOut.Add(job);
            }
            else
            {
                mapStates.Add(job.SourceEndpoint, EndpointState.AccessState.Reading);
                mapStates.Add(job.DestinationEndpoint, EndpointState.AccessState.Writing);
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
        string endpoint,
        SortedDictionary<string, EndpointState.AccessState> mapStates)
    {
        foreach (var kvp in mapStates)
        {
            bool isWriting = kvp.Value == EndpointState.AccessState.Writing;
            if (isWriting)
            {
                bool isShared = endpoint.Contains(kvp.Key) || kvp.Key.Contains(endpoint);
                if (isShared)
                {
                    _logger.LogInformation($"Cannot use source endpoint {kvp.Key} because it already is in use.");
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
        string endpoint,
        SortedDictionary<string, EndpointState.AccessState> mapStates)
    {
        foreach (var kvp in mapStates)
        {
            bool isShared = endpoint.Contains(kvp.Key) || kvp.Key.Contains(endpoint);
            if (isShared)
            {
                _logger.LogInformation($"Cannot use destination endpoint {kvp.Key} because it already is in use.");
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