using Hannibal.Models;
using Hannibal.Services;

namespace Hannibal.Client;

public interface IHannibalServiceClient
{
    public Task<Job> GetJobAsync(int jobId);

    public Task<IEnumerable<Job>> GetJobsAsync(ResultPage resultPage, JobFilter filter);
    public Task<Job> AcquireNextJobAsync(AcquireParams acquireParams);
    public Task<Result> ReportJobAsync(JobStatus jobStatus);
    public Task<ShutdownResult> ShutdownAsync();
}