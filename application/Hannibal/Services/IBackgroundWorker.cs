using Hannibal.Models;

namespace Hannibal.Services;

public interface IBackgroundWorker
{
    Task<RunnerResult> StartBackgroundServiceAsync(
        RCloneServiceParams rCloneServiceParams,
        CancellationToken cancellationToken);
    Task<RunnerResult> StopBackgroundServiceAsync(
        RCloneServiceParams rCloneServiceParams,
        CancellationToken cancellationToken);
}