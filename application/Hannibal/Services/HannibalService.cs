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

    
    public async Task<Job> AcquireNextJobAsync(string capabilities, string owner, CancellationToken cancellationToken)
    {
        _logger.LogInformation("new job requested by for client with capas {capabilities}", capabilities);

        int jobId;
        lock (_lo)
        {
            jobId = ++_nextId;
        }
        return new Job 
        { 
            Id = jobId, FromUri = "file:///tmp/a", ToUri = "file:///tmp/b", State = Job.JobState.Ready 
        };
    }


    public async Task<Result> ReportJobAsync(JobStatus jobStatus, CancellationToken cancellationToken)
    {
        _logger.LogInformation("job {jobId} reported back status {jobStatus}", jobStatus.JobId, jobStatus.Status);

        return new Result
        {
            Status = 0
        };
    }


    public async Task<ShutdownResult> ShutdownAsync(CancellationToken cancellationTokens)
    {
        return new ShutdownResult() { ErrorCode = 0 };
    }
}