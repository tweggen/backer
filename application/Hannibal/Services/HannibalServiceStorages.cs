using Hannibal.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hannibal.Services;

public partial class HannibalService
{
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

    public async Task<CreateStorageResult> CreateStorageAsync(
        Storage storage,
        CancellationToken cancellationToken)
    {
        await _obtainUser();
        
        storage.UserId = _currentUser.Id;
        
        await _context.Storages.AddAsync(storage, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        return new CreateStorageResult() { Id = storage.Id };
    }

    public async Task DeleteStorageAsync(
        int id,
        CancellationToken cancellationToken)
    {
        var storage = await _context.Storages.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (storage == null)
        {
            throw new KeyNotFoundException($"No storage found for id {id}");
        }

        _context.Storages.Remove(storage);
        await _context.SaveChangesAsync(cancellationToken);
    }

    
    public async Task<Storage> UpdateStorageAsync(
        int id,
        Storage updatedStorage,
        CancellationToken cancellationToken)
    {
        var storage = await _context.Storages
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
            
        if (storage == null)
        {
            throw new KeyNotFoundException($"No storage found for id {id}");
        }
        
        // Verify the new user exists if it's being changed
        if (updatedStorage.UserId != storage.UserId)
        {
            throw new InvalidDataException($"Unable to change user id");
        }

        // Track if tokens changed for reauthentication notification
        bool tokensChanged = false;
        if (!string.IsNullOrEmpty(updatedStorage.AccessToken) && 
            storage.AccessToken != updatedStorage.AccessToken)
        {
            tokensChanged = true;
        }
        if (!string.IsNullOrEmpty(updatedStorage.RefreshToken) && 
            storage.RefreshToken != updatedStorage.RefreshToken)
        {
            tokensChanged = true;
        }

        storage.Technology = updatedStorage.Technology;
        storage.UriSchema = updatedStorage.UriSchema;
        storage.Networks = updatedStorage.Networks;
        storage.OAuth2Email = updatedStorage.OAuth2Email;
        storage.ClientId = updatedStorage.ClientId;
        storage.ClientSecret = updatedStorage.ClientSecret;
        storage.AccessToken = updatedStorage.AccessToken;
        storage.RefreshToken = updatedStorage.RefreshToken;
        storage.ExpiresAt = updatedStorage.ExpiresAt.ToUniversalTime();

        await _context.SaveChangesAsync(cancellationToken);
        
        /*
         * At this point we must inform the local instances that the storage
         * config has changed - but only if tokens actually changed
         */
        if (tokensChanged)
        {
            _logger.LogInformation($"Storage {storage.UriSchema} tokens updated, notifying agents");
            await _hannibalHub.Clients.All.SendAsync(
                "StorageReauthenticated", 
                storage.UriSchema, 
                cancellationToken);
        }
        
        return storage;
    }

}