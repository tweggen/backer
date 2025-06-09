using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Hannibal.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hannibal.Services;

public enum MergeStrategy
{
    SkipExisting,      // Skip items that already exist
    UpdateExisting,    // Update existing items with new data
    ReplaceExisting,   // Delete existing and create new
    CreateNew          // Always create new (with modified names if needed)
}


public class ImportResult
{
    public int StoragesCreated { get; set; }
    public int StoragesUpdated { get; set; }
    public int StoragesSkipped { get; set; }
    public int EndpointsCreated { get; set; }
    public int EndpointsUpdated { get; set; }
    public int EndpointsSkipped { get; set; }
    
    public int TotalProcessed => StoragesCreated + StoragesUpdated + StoragesSkipped + 
                                 EndpointsCreated + EndpointsUpdated + EndpointsSkipped;
}


// Assuming these are your entity models - adjust as needed
public class ConfigExport
{
    public string ExportedAt { get; set; }
    public string ExportedBy { get; set; }
    public string Version { get; set; }
    public List<StorageExport> Storages { get; set; } = new();
    public List<EndpointExport> Endpoints { get; set; } = new();
}


public class StorageExport
{
    public int Id { get; set; }
    public string UserId { get; set; }
    public string Technology { get; set; }
    public string UriSchema { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public bool IsActive { get; set; }
}


public class EndpointExport
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string UserId { get; set; }
    public int? StorageId { get; set; }
    public string Path { get; set; }
    public string Comment { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public bool IsActive { get; set; }
}


public partial class HannibalService
{
    /// <summary>
    /// Exports configuration data (Storages and Endpoints) for the specified user or current user
    /// </summary>
    /// <param name="userId">User ID to export data for. If null, exports for current user.</param>
    /// <param name="includeInactive">Whether to include inactive/deleted items</param>
    /// <returns>JSON string containing the exported configuration</returns>
    public async Task<ConfigExport> ExportConfig(bool includeInactive, CancellationToken cancellationToken)
    {
        await _obtainUser();
        
        try
        {
            _logger?.LogInformation("Starting config export for user {UserId}", _currentUser.Id);

            // Query storages for the user
            var storagesQuery = _context.Storages.Where(s => s.UserId == _currentUser.Id);
            if (!includeInactive)
            {
                storagesQuery = storagesQuery.Where(s => s.IsActive);
            }
            
            var storages = await storagesQuery
                .Select(s => new StorageExport
                {
                    Id = s.Id,
                    Technology = s.Technology,
                    UriSchema = s.UriSchema,
                    UserId = s.UserId,
                    CreatedAt = s.CreatedAt,
                    UpdatedAt = s.UpdatedAt,
                    IsActive = s.IsActive
                })
                .ToListAsync();

            // Query endpoints for the user
            var endpointsQuery = _context.Endpoints.Where(e => e.UserId == _currentUser.Id);
            if (!includeInactive)
            {
                endpointsQuery = endpointsQuery.Where(e => e.IsActive);
            }

            var endpoints = await endpointsQuery
                .Select(e => new EndpointExport
                {
                    Id = e.Id,
                    Name = e.Name,
                    UserId = e.UserId,
                    StorageId = e.StorageId,
                    Path = e.Path,
                    Comment = e.Comment,
                    CreatedAt = e.CreatedAt,
                    UpdatedAt = e.UpdatedAt,
                    IsActive = e.IsActive
                })
                .ToListAsync();

            var export = new ConfigExport
            {
                ExportedAt = DateTime.UtcNow.ToString("O"),
                ExportedBy = _currentUser.Id,
                Version = "1.0", // You might want to track versions
                Storages = storages,
                Endpoints = endpoints
            };

            #if false
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var json = JsonSerializer.Serialize(export, jsonOptions);
            
            _logger?.LogInformation("Config export completed for user {Username}. Exported {StorageCount} storages and {EndpointCount} endpoints", 
                user.Username, storages.Count, endpoints.Count);

            return json;
            #else
            return export;
            #endif
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during config export for user {Username}", "timo");
            throw;
        }
    }

    /// <summary>
    /// Imports configuration data from a previous export, merging it with existing data
    /// </summary>
    /// <param name="configJson">JSON string from a previous export</param>
    /// <param name="mergeStrategy">Strategy for handling conflicts</param>
    /// <param name="targetUserId">Target user ID for import. If null, uses current user.</param>
    /// <returns>Import result summary</returns>
    public async Task<ImportResult> ImportConfig(string configJson, MergeStrategy mergeStrategy, CancellationToken cancellationToken)
    {
        try
        {
            _logger?.LogInformation("Starting config import for user {UserId}", _currentUser.Id);

            var import = JsonSerializer.Deserialize<ConfigExport>(configJson, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            if (import == null)
            {
                throw new ArgumentException("Invalid configuration JSON");
            }

            var result = new ImportResult();

            using var transaction = await _context.Database.BeginTransactionAsync();
            
            try
            {
                // Import Storages
                await ImportStorages(import.Storages, _currentUser.Id, mergeStrategy, result);
                
                // Import Endpoints (after storages to handle dependencies)
                await ImportEndpoints(import.Endpoints, _currentUser.Id, mergeStrategy, result);

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                _logger?.LogInformation("Config import completed for user {UserId}. Results: {Results}", 
                    _currentUser.Id, JsonSerializer.Serialize(result));

                return result;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during config import for user {Username}", "timo");
            throw;
        }
    }

    
    private async Task ImportStorages(List<StorageExport> storages, string userId, MergeStrategy mergeStrategy, ImportResult result)
    {
        foreach (var storageExport in storages)
        {
            // Check if storage already exists (by name and user)
            var existing = await _context.Storages
                .FirstOrDefaultAsync(s => s.Technology == storageExport.Technology && s.UserId == userId);

            if (existing != null)
            {
                switch (mergeStrategy)
                {
                    case MergeStrategy.SkipExisting:
                        result.StoragesSkipped++;
                        continue;
                        
                    case MergeStrategy.UpdateExisting:
                        existing.UriSchema = storageExport.UriSchema;
                        existing.Technology = storageExport.Technology;
                        existing.UpdatedAt = DateTime.UtcNow;
                        existing.IsActive = storageExport.IsActive;
                        result.StoragesUpdated++;
                        break;
                        
                    case MergeStrategy.ReplaceExisting:
                        _context.Storages.Remove(existing);
                        goto case MergeStrategy.CreateNew;
                        
                    case MergeStrategy.CreateNew:
                        var newStorage = new Storage // Adjust to your actual entity type
                        {
                            Technology = storageExport.Technology,
                            UriSchema = storageExport.UriSchema,
                            CreatedAt = DateTime.UtcNow,
                            IsActive = storageExport.IsActive
                        };
                        _context.Storages.Add(newStorage);
                        result.StoragesCreated++;
                        break;
                }
            }
            else
            {
                // Create new storage
                var newStorage = new Storage // Adjust to your actual entity type
                {
                    UserId = userId,
                    Technology = storageExport.Technology,
                    UriSchema = storageExport.UriSchema,
                    CreatedAt = DateTime.UtcNow,
                    IsActive = storageExport.IsActive
                };
                _context.Storages.Add(newStorage);
                result.StoragesCreated++;
            }
        }
    }

    
    private async Task ImportEndpoints(List<EndpointExport> endpoints, string userId, MergeStrategy mergeStrategy, ImportResult result)
    {
        foreach (var endpointExport in endpoints)
        {
            // Check if endpoint already exists (by name and user)
            var existing = await _context.Endpoints
                .FirstOrDefaultAsync(e => e.Name == endpointExport.Name && e.UserId == userId);

            // Resolve storage reference if present
            int? storageId = null;
            if (endpointExport.StorageId.HasValue)
            {
                // Try to find storage by original ID first, then by Technology
                var storage = await _context.Storages
                    .FirstOrDefaultAsync(s => s.UserId == userId && 
                        (s.Id == endpointExport.StorageId.Value || 
                         _context.Storages.Any(orig => orig.Id == endpointExport.StorageId.Value && orig.Technology == s.Technology)));
                storageId = storage?.Id;
            }

            if (existing != null)
            {
                switch (mergeStrategy)
                {
                    case MergeStrategy.SkipExisting:
                        result.EndpointsSkipped++;
                        continue;
                        
                    case MergeStrategy.UpdateExisting:
                        existing.StorageId = storageId.Value;
                        existing.Path = endpointExport.Path;
                        existing.Comment = endpointExport.Comment;
                        existing.UpdatedAt = DateTime.UtcNow;
                        existing.IsActive = endpointExport.IsActive;
                        result.EndpointsUpdated++;
                        break;
                        
                    case MergeStrategy.ReplaceExisting:
                        _context.Endpoints.Remove(existing);
                        goto case MergeStrategy.CreateNew;
                        
                    case MergeStrategy.CreateNew:
                        var newEndpoint = new Endpoint // Adjust to your actual entity type
                        {
                            Name = GetUniqueEndpointName(endpointExport.Name, userId),
                            UserId = userId,
                            StorageId = storageId.Value,
                            Path = endpointExport.Path,
                            Comment = endpointExport.Comment,
                            CreatedAt = DateTime.UtcNow,
                            IsActive = endpointExport.IsActive
                        };
                        _context.Endpoints.Add(newEndpoint);
                        result.EndpointsCreated++;
                        break;
                }
            }
            else
            {
                // Create new endpoint
                var newEndpoint = new Endpoint // Adjust to your actual entity type
                {
                    Name = endpointExport.Name,
                    UserId = userId,
                    StorageId = storageId.Value,
                    Path = endpointExport.Path,
                    Comment = endpointExport.Comment,
                    CreatedAt = DateTime.UtcNow,
                    IsActive = endpointExport.IsActive
                };
                _context.Endpoints.Add(newEndpoint);
                result.EndpointsCreated++;
            }
        }
    }


    private string GetUniqueEndpointName(string baseName, string userId)
    {
        var counter = 1;
        var name = baseName;
        
        while (_context.Endpoints.Any(e => e.Name == name && e.UserId == userId))
        {
            name = $"{baseName} ({counter})";
            counter++;
        }
        
        return name;
    }

    
    /// <summary>
    /// Exports configuration to a file
    /// </summary>
    public async Task<string> ExportConfigToFile(string filePath, bool includeInactive = false)
    {
        var export = await ExportConfig(includeInactive, CancellationToken.None);
        
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var jsonExport = JsonSerializer.Serialize(export, jsonOptions);

        await File.WriteAllTextAsync(filePath, jsonExport);
        return filePath;
    }

    
    /// <summary>
    /// Imports configuration from a file
    /// </summary>
    public async Task<ImportResult> ImportConfigFromFile(string filePath, MergeStrategy mergeStrategy = MergeStrategy.SkipExisting)
    {
        var config = await File.ReadAllTextAsync(filePath);
        return await ImportConfig(config, mergeStrategy, CancellationToken.None);
    }
}

