using System.Text.RegularExpressions;
using Hannibal.Models;
using Microsoft.EntityFrameworkCore;

namespace Hannibal.Services;

public partial class HannibalService
{
    private static readonly Regex _gitOwnerRepoPathPattern = new(@"^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    /**
     * Only git endpoints are validated - endpoints of every other technology
     * keep their historical free-text behaviour (plan-git-repo-storage.md
     * Gate A). Path becomes part of a filesystem cache path and, for URL
     * hosts, an "owner/repo" pair sent to a forge, so both need policing here
     * rather than left to the git worker to discover at run time.
     */
    private static void _validateEndpoint(Storage storage, string path)
    {
        if (storage.Technology != "git")
        {
            return;
        }

        /*
         * Split on both separators: a filesystem-root host skips the
         * owner/repo shape check below, so a backslash traversal segment
         * would otherwise slip through on Windows.
         */
        if (path.Split('/', '\\').Any(segment => segment == ".."))
        {
            throw new ArgumentException($"Path '{path}' must not contain '..' segments.");
        }

        bool hostIsUrl = Uri.TryCreate(storage.Host, UriKind.Absolute, out var hostUri)
                          && (hostUri.Scheme == Uri.UriSchemeHttp || hostUri.Scheme == Uri.UriSchemeHttps);
        if (hostIsUrl && !_gitOwnerRepoPathPattern.IsMatch(path))
        {
            throw new ArgumentException($"Path '{path}' must be exactly 'owner/repo' for a git storage with a URL host.");
        }
    }

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

        _validateEndpoint(storage, endpoint.Path);

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

        _validateEndpoint(endpoint.Storage, updatedEndpoint.Path);

        // Update other properties
        endpoint.Name = updatedEndpoint.Name;
        endpoint.Path = updatedEndpoint.Path;
        endpoint.Comment = updatedEndpoint.Comment;

        await _context.SaveChangesAsync(cancellationToken);
        return endpoint;
    }
}