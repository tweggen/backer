using Hannibal.Models;
using Result = WorkerRClone.Models.Result;

namespace WorkerRClone;

public interface IRCloneService
{
    public Task<Result> GetStatus(int jobId, CancellationToken cancellationToken);
    public Task<Job> GetCurrentJob(CancellationToken cancellationToken);
}