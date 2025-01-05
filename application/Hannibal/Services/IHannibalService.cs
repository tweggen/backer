using Hannibal.Models;

namespace Hannibal.Services;

public interface IHannibalService
{
    public Task<Job> AcquireNextJobAsync(string capabilities, CancellationToken cancellationToken);
    public Task<ShutdownResult> ShutdownAsync();
}