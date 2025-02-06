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


    public async Task<Result> ReportJobAsync(JobStatus jobStatus, CancellationToken cancellationToken)
    {
        _logger.LogInformation("job {jobId} reported back status {jobStatus}", jobStatus.JobId, jobStatus.Status);

        int result;
        
        var job = await _context.Jobs.FirstOrDefaultAsync(
            j => j.State == Job.JobState.Executing && j.Id == jobStatus.JobId, cancellationToken);
        if (job != null)
        {
            /*
             * Save back the status to the database.
             */
            if (jobStatus.Status >= 0)
            {
                _logger.LogInformation("job {jobId} is done", jobStatus.JobId);
                job.State = Job.JobState.DoneSuccess;
            }
            else
            {
                _logger.LogInformation("job {jobId} is not done", jobStatus.JobId);
                /*
                 * Not done. We should check if we should leave it as DoneSuccess or DoneError.
                 */
                job.State = Job.JobState.Ready;
            }

            job.Owner = "";
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

        /*
         * Inform all workers there might be a new job available right now.
         */
        await _hannibalHub.Clients.All.SendAsync("NewJobAvailable");
        
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