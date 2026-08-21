using System.Text.RegularExpressions;
using Hannibal.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hannibal.Services;

public partial class HannibalService
{
    private static readonly Regex _gitUriSchemaPattern = new(@"^[a-z0-9][a-z0-9_-]*$", RegexOptions.Compiled);

    /**
     * The codebase's first storage validation (plan-git-repo-storage.md Gate
     * A). Technology must be one of the known technologies; a git storage
     * additionally needs a real Host and a UriSchema that is well-formed and
     * unique per user, because UriSchema becomes both the rclone remote name
     * (for rclone technologies) and the git worker's cache directory name.
     * Throws ArgumentException naming the offending field - Api/Program.cs
     * maps that to a 400 with the message.
     */
    private async Task _validateStorageAsync(
        string technology,
        string? host,
        string? uriSchema,
        string userId,
        int? excludeId,
        CancellationToken cancellationToken)
    {
        if (!Technologies.GetTechnologies().Contains(technology))
        {
            throw new ArgumentException($"Technology '{technology}' is not a known storage technology.");
        }

        if (technology != "git")
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Host must not be empty for a git storage.");
        }

        bool isHttpUrl = Uri.TryCreate(host, UriKind.Absolute, out var hostUri)
                          && (hostUri.Scheme == Uri.UriSchemeHttp || hostUri.Scheme == Uri.UriSchemeHttps);
        bool isFsRoot = Path.IsPathFullyQualified(host);
        if (!isHttpUrl && !isFsRoot)
        {
            throw new ArgumentException(
                $"Host '{host}' must be an absolute http/https URL or an absolute filesystem root for a git storage.");
        }

        if (string.IsNullOrEmpty(uriSchema) || !_gitUriSchemaPattern.IsMatch(uriSchema))
        {
            throw new ArgumentException(
                $"UriSchema '{uriSchema}' must match ^[a-z0-9][a-z0-9_-]*$ for a git storage.");
        }

        var collision = await _context.Storages.AnyAsync(
            s => s.UserId == userId
                 && (!excludeId.HasValue || s.Id != excludeId.Value)
                 && s.UriSchema.ToLower() == uriSchema.ToLower(),
            cancellationToken);
        if (collision)
        {
            throw new ArgumentException(
                $"UriSchema '{uriSchema}' is already used by another storage of this user.");
        }
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

    public async Task<CreateStorageResult> CreateStorageAsync(
        Storage storage,
        CancellationToken cancellationToken)
    {
        await _obtainUser();

        storage.UserId = _currentUser.Id;

        await _validateStorageAsync(
            storage.Technology, storage.Host, storage.UriSchema, storage.UserId, null, cancellationToken);

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

        await _validateStorageAsync(
            updatedStorage.Technology, updatedStorage.Host, updatedStorage.UriSchema, storage.UserId, id,
            cancellationToken);

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