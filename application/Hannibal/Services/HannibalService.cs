using System.Collections.Generic;
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
        if (null == list)
        {
            return new List<Job>();
        }
        else
        {
            return list;
        }
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

        // TXWTODO: Currently we only have one backend with one technology, so we take everything.
        var job = await _context.Jobs.FirstOrDefaultAsync(j => 
                j.State == Job.JobState.Ready 
                && j.Owner == "",
            cancellationToken);
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