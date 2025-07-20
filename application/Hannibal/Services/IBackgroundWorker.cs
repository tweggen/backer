using Hannibal.Models;
using Microsoft.Extensions.Hosting;

namespace Hannibal.Services;

public interface IBackgroundWorker : IHostedService
{
    public Task<RunnerResult> GetRunnerStatusAsync(
        CancellationToken cancellationToken);
    public Task<RunnerResult> StartBackgroundServiceAsync(
        RCloneServiceParams rCloneServiceParams,
        CancellationToken cancellationToken);
    public Task<RunnerResult> StopBackgroundServiceAsync(
        CancellationToken cancellationToken);
}