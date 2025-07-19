using Hannibal.Models;
using Microsoft.Extensions.Hosting;

namespace Hannibal.Services;

public interface IBackgroundWorker : IHostedService
{
    Task<RunnerResult> StartBackgroundServiceAsync(
        RCloneServiceParams rCloneServiceParams,
        CancellationToken cancellationToken);
    Task<RunnerResult> StopBackgroundServiceAsync(
        CancellationToken cancellationToken);
}