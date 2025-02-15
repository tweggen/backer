using Hannibal.Models;

namespace Hannibal.Services;

public interface IHannibalService
{
    public Task<Job> GetJobAsync(int jobId, CancellationToken cancellationToken);

    public Task<IEnumerable<Job>> GetJobsAsync(ResultPage resultPage, JobFilter filter, CancellationToken cancellationToken);

    public Task<Job> AcquireNextJobAsync(AcquireParams acquireParams, CancellationToken cancellationToken);
    
    /**
     * Report the current state of the job.
     *
     * Note, that if a job does not report its current state for a certain timeout span,
     * it is considered to be dead.
     */
    public Task<Result> ReportJobAsync(JobStatus jobStatus, CancellationToken cancellationToken);
    public Task<ShutdownResult> ShutdownAsync(CancellationToken cancellationToken);
}