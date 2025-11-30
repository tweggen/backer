using Hannibal.Models;
using Microsoft.EntityFrameworkCore;

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

        // Update other properties
        storage.Technology = updatedStorage.Technology;
        storage.UriSchema = updatedStorage.UriSchema;
        storage.Networks = updatedStorage.Networks;

        await _context.SaveChangesAsync(cancellationToken);
        return storage;
    }

}