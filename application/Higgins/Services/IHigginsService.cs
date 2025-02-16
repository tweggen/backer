using Higgins.Models;

namespace Higgins.Services;

public interface IHigginsService
{
    public Task<CreateEndpointResult> CreateEndpointAsync(
        Endpoint endpoint,
        CancellationToken cancellationToken);

    public Task<Endpoint> GetEndpointAsync(
        string name,
        CancellationToken cancellationToken);

    public Task<IEnumerable<Endpoint>> GetEndpointsAsync(
        CancellationToken cancellationToken);
}