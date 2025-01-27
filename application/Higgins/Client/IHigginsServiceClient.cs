using Higgins.Models;

namespace Higgins.Client;

public interface IHigginsServiceClient
{
    public Task<CreateEndpointResult> CreateEndpointAsync(Endpoint endpoint);
    public Task<Endpoint> GetEndpointAsync(string name);
}