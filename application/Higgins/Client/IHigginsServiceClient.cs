using Higgins.Models;

namespace Higgins.Client;

public interface IHigginsServiceClient
{
    public Task<User> GetUserAsync(int id);
    public Task<CreateEndpointResult> CreateEndpointAsync(Endpoint endpoint);
    public Task<IEnumerable<Endpoint>> GetEndpointsAsync();
    public Task<Endpoint> GetEndpointAsync(string name);
    public Task<IEnumerable<Storage>> GetStoragesAsync();
    public Task<Storage> GetStorageAsync(int id);
}