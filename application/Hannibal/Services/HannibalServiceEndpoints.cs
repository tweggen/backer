using Hannibal.Models;
using Microsoft.EntityFrameworkCore;

namespace Hannibal.Services;

public partial class HannibalService
{
    public async Task<CreateEndpointResult> CreateEndpointAsync(
        Endpoint endpoint,
        CancellationToken cancellationToken)
    {
        await _obtainUser();
        
        endpoint.UserId = _currentUser.Id;
        
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
        var listEndpoints = await _context.Endpoints
            .OrderBy(e => e.Name)
            .ToListAsync(cancellationToken);

        return listEndpoints;
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

    
    public async Task<Endpoint> UpdateEndpointAsync(
        int id,
        Endpoint updatedEndpoint,
        CancellationToken cancellationToken)
    {
        var endpoint = await _context.Endpoints
            .Include(e => e.Storage)
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
            
        if (endpoint == null)
        {
            throw new KeyNotFoundException($"No endpoint found for id {id}");
        }

        // Verify the new user exists if it's being changed
        if (updatedEndpoint.UserId != endpoint.UserId)
        {
            throw new InvalidDataException($"Unable to change user id");
        }

        // Verify the new storage exists if it's being changed
        if (updatedEndpoint.StorageId != endpoint.StorageId)
        {
            var storage = await _context.Storages.FirstOrDefaultAsync(s => s.Id == updatedEndpoint.StorageId, cancellationToken);
            if (storage == null)
            {
                throw new KeyNotFoundException($"No storage found for storageid {updatedEndpoint.StorageId}");
            }
            endpoint.Storage = storage;
            endpoint.StorageId = updatedEndpoint.StorageId;
        }

        // Update other properties
        endpoint.Name = updatedEndpoint.Name;
        endpoint.Path = updatedEndpoint.Path;
        endpoint.Comment = updatedEndpoint.Comment;

        await _context.SaveChangesAsync(cancellationToken);
        return endpoint;
    }
}