using Higgins.Models;

namespace Higgins.Services;

public interface IHigginsService
{
    public Task<User> GetUserAsync(
        int id,
        CancellationToken cancellationToken);

    public Task<CreateEndpointResult> CreateEndpointAsync(
        Endpoint endpoint,
        CancellationToken cancellationToken);

    public Task<Endpoint> GetEndpointAsync(
        string name,
        CancellationToken cancellationToken);

    public Task<IEnumerable<Endpoint>> GetEndpointsAsync(
        CancellationToken cancellationToken);
    
    public Task<Storage> GetStorageAsync(
        int id,
        CancellationToken cancellationToken);

    public Task<IEnumerable<Storage>> GetStoragesAsync(
        CancellationToken cancellationToken);

}