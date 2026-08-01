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

    
    /**
     * Compare an old and a new token value, treating null and empty as
     * equivalent. Setting, replacing and clearing a token all count as a
     * change, having no token before and after does not.
     */
    private static bool _isTokenChanged(string? oldToken, string? newToken)
    {
        if (string.IsNullOrEmpty(oldToken) && string.IsNullOrEmpty(newToken))
        {
            return false;
        }

        return oldToken != newToken;
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

        /*
         * Callers inside this service may hand us the very entity instance that
         * already is tracked by our context, after having modified it in place
         * (that is what the OAuth2 result handler does). In that case the query
         * above returns that same instance, so every old-vs-new comparison below
         * would compare the object with itself and could never detect anything.
         */
        bool isSelfUpdate = ReferenceEquals(storage, updatedStorage);

        // Verify the new user exists if it's being changed
        if (updatedStorage.UserId != storage.UserId)
        {
            throw new InvalidDataException($"Unable to change user id");
        }

        // Track if tokens changed for reauthentication notification.
        // Note that clearing a previously set token (OAuth2 disconnect) counts
        // as a change as well.
        bool tokensChanged = false;
        if (_isTokenChanged(storage.AccessToken, updatedStorage.AccessToken))
        {
            tokensChanged = true;
        }
        if (_isTokenChanged(storage.RefreshToken, updatedStorage.RefreshToken))
        {
            tokensChanged = true;
        }

        // Track if credentials changed for notification
        bool credentialsChanged = tokensChanged;

        // Check credential-based fields for changes
        if (storage.Host != updatedStorage.Host ||
            storage.Username != updatedStorage.Username ||
            storage.Password != updatedStorage.Password ||
            storage.Domain != updatedStorage.Domain ||
            storage.Port != updatedStorage.Port)
        {
            credentialsChanged = true;
        }

        if (isSelfUpdate)
        {
            /*
             * We cannot tell what has been modified in place, so we have to
             * assume the credentials did change - this code path exists for
             * writing freshly obtained OAuth2 tokens.
             */
            credentialsChanged = true;
        }

        // Update common fields
        storage.Technology = updatedStorage.Technology;
        storage.UriSchema = updatedStorage.UriSchema;
        storage.Networks = updatedStorage.Networks;
        
        // Update OAuth fields
        storage.OAuth2Email = updatedStorage.OAuth2Email;
        storage.ClientId = updatedStorage.ClientId;
        storage.ClientSecret = updatedStorage.ClientSecret;
        storage.AccessToken = updatedStorage.AccessToken;
        storage.RefreshToken = updatedStorage.RefreshToken;
        storage.ExpiresAt = updatedStorage.ExpiresAt.ToUniversalTime();
        
        // Update credential-based fields (SMB, FTP, etc.)
        storage.Host = updatedStorage.Host;
        storage.Username = updatedStorage.Username;
        storage.Password = updatedStorage.Password;
        storage.Domain = updatedStorage.Domain;
        storage.Port = updatedStorage.Port;

        await _context.SaveChangesAsync(cancellationToken);
        
        /*
         * At this point we must inform the local instances that the storage
         * config has changed - if tokens or credentials actually changed
         */
        if (credentialsChanged)
        {
            _logger.LogInformation($"Storage {storage.Id} ({storage.Technology}) credentials updated, notifying agents");
            await _hannibalHub.Clients.All.SendAsync(
                "StorageReauthenticated", 
                storage.UriSchema, 
                cancellationToken);
        }
        
        return storage;
    }

}