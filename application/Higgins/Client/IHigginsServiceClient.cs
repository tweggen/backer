using Higgins.Models;

namespace Higgins.Client;

public interface IHigginsServiceClient
{
    public Task<User> GetUserAsync(int id, CancellationToken cancellationToken);
    public Task<CreateEndpointResult> CreateEndpointAsync(Endpoint endpoint, CancellationToken cancellationToken);
    public Task<IEnumerable<Endpoint>> GetEndpointsAsync(CancellationToken cancellationToken);
    public Task<Endpoint> GetEndpointAsync(string name, CancellationToken cancellationToken);
    public Task<IEnumerable<Storage>> GetStoragesAsync(CancellationToken cancellationToken);
    public Task<Storage> GetStorageAsync(int id, CancellationToken cancellationToken);
    public Task DeleteEndpointAsync(int id, CancellationToken cancellationToken);
    public Task<Endpoint> UpdateEndpointAsync(int id, Endpoint endpoint, CancellationToken cancellationToken);
}