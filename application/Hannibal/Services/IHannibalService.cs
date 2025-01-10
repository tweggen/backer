using Hannibal.Models;

namespace Hannibal.Services;

public interface IHannibalService
{
    public Task<Job> GetJobAsync(int jobId, CancellationToken cancellationToken);

    public Task<IEnumerable<Job>> GetJobsAsync(ResultPage resultPage, JobFilter filter, CancellationToken cancellationToken);
    public Task<Job> AcquireNextJobAsync(string capabilities, string owner, CancellationToken cancellationToken);
    public Task<Result> ReportJobAsync(JobStatus jobStatus, CancellationToken cancellationToken);
    public Task<ShutdownResult> ShutdownAsync();
}