using Hannibal.Configuration;
using Hannibal.Data;
using Hannibal.Models;
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


    /**
     * Until we have a real database backend, we fake new entries using _nextId.
     */
    private static int _nextId;

    public HannibalService(
        HannibalContext context,
        ILogger<HannibalService> logger,
        IOptions<HannibalServiceOptions> options)
    {
        _context = context;
        _logger = logger;
        _options = options.Value;
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

        var list = await _context.Jobs.ToListAsync();
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
        await _ensureRequiredJobs();
        
        _logger.LogInformation("new job requested by for client with capas {capabilities}", acquireParams.Capabilities);

        var job = await _context.Jobs.FirstOrDefaultAsync(j => j.State == Job.JobState.Ready && j.Owner == "",
            cancellationToken);
        if (job != null)
        {
            job.Owner = acquireParams.Owner;
            job.State = Job.JobState.Executing;
            _context.Update(job);
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

        var job = await _context.Jobs.FirstOrDefaultAsync(
            j => j.State == Job.JobState.Executing && j.Id == jobStatus.JobId, cancellationToken);
        if (job != null)
        {
            /*
             * Save back the status to the database.
             */
            if (jobStatus.Status >= 0)
            {
                job.State = Job.JobState.DoneSuccess;
            }
            else
            {
                /*
                 * Not done. We should check if we should leave it as DoneSuccess or DoneError.
                 */
                job.State = Job.JobState.Ready;
            }

            job.Owner = "";
            _context.Update(job);
            await _context.SaveChangesAsync(cancellationToken);
        }
        else
        {
            throw new KeyNotFoundException($"No job found for jobId {jobStatus.JobId} that is executing.");
        }

        return new Result
        {
            Status = 0
        };
    }


    public async Task<ShutdownResult> ShutdownAsync(CancellationToken cancellationTokens)
    {
        return new ShutdownResult() { ErrorCode = 0 };
    }


    /**
     * This is a hardcoded logic to make sure a certain set of jobs is created daily or weekly.
     * This should be replaced by a more refined logic deriving these jobs from the higgins definitions
     * and the user jobs supplied.
     *
     * The way we do it right now is neither scalable nor good architecture.
     */
    private async Task _ensureRequiredJobs()
    {
        List<Job> listDailyJobs = new()
        {
            new()
            {
                Tag = "DailyTmp",
                EndBy = DateTime.MinValue + TimeSpan.FromHours(24 + 3),
                StartFrom = DateTime.MinValue + TimeSpan.FromHours(3),
                FromUri = "/tmp/a",
                ToUri = "/tmp/b"
            }
        };

        foreach (var daily in listDailyJobs)
        {
            var job = await _context.Jobs.FirstOrDefaultAsync(
                j => j.State == Job.JobState.Ready && j.Tag == daily.Tag && j.StartFrom > DateTime.Today);
            if (job == null)
            {
                _logger.LogInformation($"Adding template job {daily}");
                await _context.Jobs.AddAsync(new Job(daily)
                {
                    StartFrom = DateTime.Today + (daily.StartFrom - DateTime.MinValue),
                    EndBy = DateTime.Today + (daily.EndBy - DateTime.MinValue),
                    Owner = "",
                    State = Job.JobState.Ready
                });
            }
        }

        await _context.SaveChangesAsync();
    }
}