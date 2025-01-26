using Higgins.Models;

namespace Higgins.Services;

public interface IHigginsService
{
    public Task<CreateEndpointResult> CreateEndpointAsync(
        Endpoint endpoint,
        CancellationToken cancellationToken);
}