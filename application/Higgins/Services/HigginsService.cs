using Higgins.Configuration;
using Higgins.Data;
using Higgins.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Higgins.Services;

public class HigginsService : IHigginsService
{
    private readonly HigginsContext _context;
    private readonly ILogger<HigginsService> _logger;
    private readonly HigginsServiceOptions _options;

    
    public HigginsService(
        HigginsContext context,
        ILogger<HigginsService> logger,
        IOptions<HigginsServiceOptions> options)
    {
        _context = context;
        _logger = logger;
        _options = options.Value;
    }


    public async Task<User> GetUserAsync(int id, CancellationToken cancellationToken)
    {
        User? user = await _context.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (null == user)
        {
            throw new KeyNotFoundException($"No user found for id {id}");
        }

        return user;
    }

    public async Task<CreateEndpointResult> CreateEndpointAsync(
        Endpoint endpoint,
        CancellationToken cancellationToken)
    {
        var user = await _context.Users.FirstAsync(u => u.Id == endpoint.UserId, cancellationToken);
        if (null == user)
        {
            throw new KeyNotFoundException($"No user found for userid {endpoint.UserId}");
        }
        endpoint.User = user;
        
        var storage = await _context.Storages.FirstAsync(s => s.Id == endpoint.StorageId, cancellationToken);
        if (null == storage)
        {
            throw new KeyNotFoundException($"No storage found for storageid {endpoint.StorageId}");
        }
        endpoint.Storage = storage;

        
        
        await _context.Endpoints.AddAsync(endpoint, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        return new CreateEndpointResult() { Id = endpoint.Id };
    }


    public async Task<Endpoint> GetEndpointAsync(
        string name,
        CancellationToken cancellationToken)
    {
        Endpoint? endpoint = await _context.Endpoints.FirstOrDefaultAsync(e => e.Name == name, cancellationToken);
        if (null == endpoint)
        {
            throw new KeyNotFoundException($"No endpoint found for name {name}");
        }

        return endpoint;
    }
    

    public async Task<IEnumerable<Endpoint>> GetEndpointsAsync(
        CancellationToken cancellationToken)
    {
        var listEndpoints = await _context.Endpoints.ToListAsync(cancellationToken);

        return listEndpoints;
    }


    public async Task<Storage> GetStorageAsync(
        int id,
        CancellationToken cancellationToken)
    {
        Storage? storage = await _context.Storages.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (null == storage)
        {
            throw new KeyNotFoundException($"No storage found for name {id}");
        }

        return storage;
    }
    
    
    public async Task<IEnumerable<Storage>> GetStoragesAsync(
        CancellationToken cancellationToken)
    {
        var listStorages = await _context.Storages.ToListAsync(cancellationToken);

        return listStorages;
    }
    

    public async Task DeleteEndpointAsync(
        int id,
        CancellationToken cancellationToken)
    {
        var endpoint = await _context.Endpoints.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (endpoint == null)
        {
            throw new KeyNotFoundException($"No endpoint found for id {id}");
        }

        _context.Endpoints.Remove(endpoint);
        await _context.SaveChangesAsync(cancellationToken);
    }
}