using System.Text.Json;
using System.Text.Json.Serialization;
using Hannibal.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hannibal.Services;

/// <summary>
/// Strategy for handling conflicts during import
/// </summary>
public enum MergeStrategy
{
    /// <summary>Skip items that already exist (match by unique name)</summary>
    SkipExisting,
    /// <summary>Update existing items with imported data</summary>
    UpdateExisting,
    /// <summary>Delete all existing data and import fresh</summary>
    ReplaceAll
}

/// <summary>
/// Result of an import operation
/// </summary>
public class ImportResult
{
    public int StoragesCreated { get; set; }
    public int StoragesUpdated { get; set; }
    public int StoragesSkipped { get; set; }
    public int EndpointsCreated { get; set; }
    public int EndpointsUpdated { get; set; }
    public int EndpointsSkipped { get; set; }
    public int RulesCreated { get; set; }
    public int RulesUpdated { get; set; }
    public int RulesSkipped { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();

    public bool Success => Errors.Count == 0;
}

#region Export DTOs

/// <summary>
/// Root export object containing all configuration data
/// </summary>
public class ConfigExport
{
    public string Version { get; set; } = "2.0";
    public DateTime ExportedAt { get; set; }
    public string? ExportedBy { get; set; }
    public List<StorageExport> Storages { get; set; } = new();
    public List<EndpointExport> Endpoints { get; set; } = new();
    public List<RuleExport> Rules { get; set; } = new();
}

/// <summary>
/// Storage export DTO - includes all fields needed to recreate a storage
/// </summary>
public class StorageExport
{
    // Identity
    public string UriSchema { get; set; } = "";  // Unique identifier (e.g., "nas_admin")
    public string Technology { get; set; } = ""; // smb, onedrive, dropbox, googledrive, local
    
    // Common
    public string Networks { get; set; } = "";
    public bool IsActive { get; set; } = true;
    
    // OAuth-based storage (OneDrive, Dropbox, Google Drive)
    public string? OAuth2Email { get; set; }
    // Note: AccessToken/RefreshToken are NOT exported - user must re-authenticate
    
    // Credential-based storage (SMB, FTP, etc.)
    public string? Host { get; set; }
    public int? Port { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }  // Optional - can be included or omitted
    public string? Domain { get; set; }
    
    // Metadata
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Endpoint export DTO
/// </summary>
public class EndpointExport
{
    public string Name { get; set; } = "";        // Unique identifier for this endpoint
    public string StorageRef { get; set; } = "";  // Reference to Storage.UriSchema
    public string Path { get; set; } = "";
    public string? Comment { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Rule export DTO
/// </summary>
public class RuleExport
{
    public string Name { get; set; } = "";
    public string? Comment { get; set; }
    public string SourceEndpointRef { get; set; } = "";      // Reference to Endpoint.Name
    public string DestinationEndpointRef { get; set; } = ""; // Reference to Endpoint.Name
    public string Operation { get; set; } = "Copy";          // Nop, Copy, Sync
    
    // Timing configuration (stored as readable strings)
    public string MaxDestinationAge { get; set; } = "1.00:00:00";           // TimeSpan as string
    public string MinRetryTime { get; set; } = "00:15:00";                  // TimeSpan as string
    public string MaxTimeAfterSourceModification { get; set; } = "00:30:00";// TimeSpan as string
    public string DailyTriggerTime { get; set; } = "03:00:00";              // TimeSpan as string
}

#endregion

public partial class HannibalService
{
    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Exports all configuration data for the current user
    /// </summary>
    /// <param name="includePasswords">Whether to include passwords in the export</param>
    /// <param name="includeInactive">Whether to include inactive items</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>ConfigExport object containing all data</returns>
    public async Task<ConfigExport> ExportConfigAsync(
        bool includePasswords = false,
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        await _obtainUser();

        _logger?.LogInformation("Starting config export for user {UserId}, includePasswords={IncludePasswords}, includeInactive={IncludeInactive}",
            _currentUser.Id, includePasswords, includeInactive);

        var export = new ConfigExport
        {
            ExportedAt = DateTime.UtcNow,
            ExportedBy = _currentUser.UserName ?? _currentUser.Id
        };

        // Export Storages
        var storagesQuery = _context.Storages.Where(s => s.UserId == _currentUser.Id);
        if (!includeInactive)
            storagesQuery = storagesQuery.Where(s => s.IsActive);

        var storages = await storagesQuery.ToListAsync(cancellationToken);
        foreach (var s in storages)
        {
            export.Storages.Add(new StorageExport
            {
                UriSchema = s.UriSchema,
                Technology = s.Technology,
                Networks = s.Networks,
                IsActive = s.IsActive,
                OAuth2Email = s.OAuth2Email,
                Host = s.Host,
                Port = s.Port,
                Username = s.Username,
                Password = includePasswords ? s.Password : null,
                Domain = s.Domain,
                CreatedAt = s.CreatedAt,
                UpdatedAt = s.UpdatedAt
            });
        }

        // Export Endpoints
        var endpointsQuery = _context.Endpoints
            .Include(e => e.Storage)
            .Where(e => e.UserId == _currentUser.Id);
        if (!includeInactive)
            endpointsQuery = endpointsQuery.Where(e => e.IsActive);

        var endpoints = await endpointsQuery.ToListAsync(cancellationToken);
        foreach (var e in endpoints)
        {
            export.Endpoints.Add(new EndpointExport
            {
                Name = e.Name,
                StorageRef = e.Storage?.UriSchema ?? "",
                Path = e.Path,
                Comment = e.Comment,
                IsActive = e.IsActive,
                CreatedAt = e.CreatedAt,
                UpdatedAt = e.UpdatedAt
            });
        }

        // Export Rules
        var rulesQuery = _context.Rules
            .Include(r => r.SourceEndpoint)
            .Include(r => r.DestinationEndpoint)
            .Where(r => r.UserId == _currentUser.Id);

        var rules = await rulesQuery.ToListAsync(cancellationToken);
        foreach (var r in rules)
        {
            export.Rules.Add(new RuleExport
            {
                Name = r.Name,
                Comment = r.Comment,
                SourceEndpointRef = r.SourceEndpoint?.Name ?? "",
                DestinationEndpointRef = r.DestinationEndpoint?.Name ?? "",
                Operation = r.Operation.ToString(),
                MaxDestinationAge = r.MaxDestinationAge.ToString(),
                MinRetryTime = r.MinRetryTime.ToString(),
                MaxTimeAfterSourceModification = r.MaxTimeAfterSourceModification.ToString(),
                DailyTriggerTime = r.DailyTriggerTime.ToString()
            });
        }

        _logger?.LogInformation("Config export completed: {StorageCount} storages, {EndpointCount} endpoints, {RuleCount} rules",
            export.Storages.Count, export.Endpoints.Count, export.Rules.Count);

        return export;
    }

    /// <summary>
    /// Exports configuration to JSON string
    /// </summary>
    public async Task<string> ExportConfigToJsonAsync(
        bool includePasswords = false,
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var export = await ExportConfigAsync(includePasswords, includeInactive, cancellationToken);
        return JsonSerializer.Serialize(export, ExportJsonOptions);
    }

    /// <summary>
    /// Imports configuration from JSON string
    /// </summary>
    /// <param name="json">JSON string from previous export</param>
    /// <param name="strategy">How to handle existing items</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Import result with statistics and any warnings/errors</returns>
    public async Task<ImportResult> ImportConfigAsync(
        string json,
        MergeStrategy strategy = MergeStrategy.SkipExisting,
        CancellationToken cancellationToken = default)
    {
        await _obtainUser();

        var result = new ImportResult();

        ConfigExport? import;
        try
        {
            import = JsonSerializer.Deserialize<ConfigExport>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException ex)
        {
            result.Errors.Add($"Invalid JSON format: {ex.Message}");
            return result;
        }

        if (import == null)
        {
            result.Errors.Add("Failed to parse configuration - empty or invalid");
            return result;
        }

        _logger?.LogInformation("Starting config import for user {UserId}, strategy={Strategy}, version={Version}",
            _currentUser.Id, strategy, import.Version);

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            // If ReplaceAll, delete everything first
            if (strategy == MergeStrategy.ReplaceAll)
            {
                await DeleteAllUserDataAsync(cancellationToken);
            }

            // Import in order: Storages -> Endpoints -> Rules (due to dependencies)
            var storageIdMap = await ImportStoragesAsync(import.Storages, strategy, result, cancellationToken);
            var endpointIdMap = await ImportEndpointsAsync(import.Endpoints, storageIdMap, strategy, result, cancellationToken);
            await ImportRulesAsync(import.Rules, endpointIdMap, strategy, result, cancellationToken);

            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _logger?.LogInformation("Config import completed successfully: {Result}", 
                JsonSerializer.Serialize(result, ExportJsonOptions));
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            result.Errors.Add($"Import failed: {ex.Message}");
            _logger?.LogError(ex, "Config import failed for user {UserId}", _currentUser.Id);
        }

        return result;
    }

    /// <summary>
    /// Deletes all configuration data for the current user
    /// </summary>
    private async Task DeleteAllUserDataAsync(CancellationToken cancellationToken)
    {
        // Delete in reverse dependency order: Rules -> Endpoints -> Storages
        var rules = await _context.Rules.Where(r => r.UserId == _currentUser.Id).ToListAsync(cancellationToken);
        _context.Rules.RemoveRange(rules);

        var endpoints = await _context.Endpoints.Where(e => e.UserId == _currentUser.Id).ToListAsync(cancellationToken);
        _context.Endpoints.RemoveRange(endpoints);

        var storages = await _context.Storages.Where(s => s.UserId == _currentUser.Id).ToListAsync(cancellationToken);
        _context.Storages.RemoveRange(storages);

        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Import storages and return a mapping of UriSchema -> new Storage ID
    /// </summary>
    private async Task<Dictionary<string, int>> ImportStoragesAsync(
        List<StorageExport> storages,
        MergeStrategy strategy,
        ImportResult result,
        CancellationToken cancellationToken)
    {
        var idMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var exp in storages)
        {
            if (string.IsNullOrWhiteSpace(exp.UriSchema))
            {
                result.Warnings.Add($"Skipping storage with empty UriSchema");
                continue;
            }

            var existing = await _context.Storages
                .FirstOrDefaultAsync(s => s.UserId == _currentUser.Id && s.UriSchema == exp.UriSchema, cancellationToken);

            if (existing != null)
            {
                idMap[exp.UriSchema] = existing.Id;

                if (strategy == MergeStrategy.SkipExisting)
                {
                    result.StoragesSkipped++;
                    continue;
                }

                // Update existing
                UpdateStorageFromExport(existing, exp);
                existing.UpdatedAt = DateTime.UtcNow;
                result.StoragesUpdated++;
            }
            else
            {
                // Create new
                var storage = new Storage
                {
                    UserId = _currentUser.Id,
                    UriSchema = exp.UriSchema,
                    Technology = exp.Technology,
                    Networks = exp.Networks ?? "",
                    IsActive = exp.IsActive,
                    OAuth2Email = exp.OAuth2Email ?? "",
                    Host = exp.Host ?? "",
                    Port = exp.Port,
                    Username = exp.Username ?? "",
                    Password = exp.Password ?? "",
                    Domain = exp.Domain ?? "",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                _context.Storages.Add(storage);
                await _context.SaveChangesAsync(cancellationToken); // Save to get ID
                idMap[exp.UriSchema] = storage.Id;
                result.StoragesCreated++;
            }
        }

        return idMap;
    }

    private void UpdateStorageFromExport(Storage storage, StorageExport exp)
    {
        storage.Technology = exp.Technology;
        storage.Networks = exp.Networks ?? "";
        storage.IsActive = exp.IsActive;
        storage.OAuth2Email = exp.OAuth2Email ?? "";
        storage.Host = exp.Host ?? "";
        storage.Port = exp.Port;
        storage.Username = exp.Username ?? "";
        storage.Domain = exp.Domain ?? "";
        
        // Only update password if provided in export
        if (!string.IsNullOrEmpty(exp.Password))
        {
            storage.Password = exp.Password;
        }
    }

    /// <summary>
    /// Import endpoints and return a mapping of Name -> new Endpoint ID
    /// </summary>
    private async Task<Dictionary<string, int>> ImportEndpointsAsync(
        List<EndpointExport> endpoints,
        Dictionary<string, int> storageIdMap,
        MergeStrategy strategy,
        ImportResult result,
        CancellationToken cancellationToken)
    {
        var idMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var exp in endpoints)
        {
            if (string.IsNullOrWhiteSpace(exp.Name))
            {
                result.Warnings.Add("Skipping endpoint with empty Name");
                continue;
            }

            // Resolve storage reference
            if (!storageIdMap.TryGetValue(exp.StorageRef, out var storageId))
            {
                // Try to find existing storage by UriSchema
                var existingStorage = await _context.Storages
                    .FirstOrDefaultAsync(s => s.UserId == _currentUser.Id && s.UriSchema == exp.StorageRef, cancellationToken);
                
                if (existingStorage != null)
                {
                    storageId = existingStorage.Id;
                }
                else
                {
                    result.Warnings.Add($"Endpoint '{exp.Name}' references unknown storage '{exp.StorageRef}' - skipped");
                    continue;
                }
            }

            var existing = await _context.Endpoints
                .FirstOrDefaultAsync(e => e.UserId == _currentUser.Id && e.Name == exp.Name, cancellationToken);

            if (existing != null)
            {
                idMap[exp.Name] = existing.Id;

                if (strategy == MergeStrategy.SkipExisting)
                {
                    result.EndpointsSkipped++;
                    continue;
                }

                // Update existing
                existing.StorageId = storageId;
                existing.Path = exp.Path;
                existing.Comment = exp.Comment ?? "";
                existing.IsActive = exp.IsActive;
                existing.UpdatedAt = DateTime.UtcNow;
                result.EndpointsUpdated++;
            }
            else
            {
                // Create new
                var endpoint = new Endpoint
                {
                    UserId = _currentUser.Id,
                    Name = exp.Name,
                    StorageId = storageId,
                    Path = exp.Path,
                    Comment = exp.Comment ?? "",
                    IsActive = exp.IsActive,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                _context.Endpoints.Add(endpoint);
                await _context.SaveChangesAsync(cancellationToken); // Save to get ID
                idMap[exp.Name] = endpoint.Id;
                result.EndpointsCreated++;
            }
        }

        return idMap;
    }

    /// <summary>
    /// Import rules using endpoint name mapping
    /// </summary>
    private async Task ImportRulesAsync(
        List<RuleExport> rules,
        Dictionary<string, int> endpointIdMap,
        MergeStrategy strategy,
        ImportResult result,
        CancellationToken cancellationToken)
    {
        foreach (var exp in rules)
        {
            if (string.IsNullOrWhiteSpace(exp.Name))
            {
                result.Warnings.Add("Skipping rule with empty Name");
                continue;
            }

            // Resolve endpoint references
            int? sourceEndpointId = await ResolveEndpointIdAsync(exp.SourceEndpointRef, endpointIdMap, cancellationToken);
            int? destEndpointId = await ResolveEndpointIdAsync(exp.DestinationEndpointRef, endpointIdMap, cancellationToken);

            if (sourceEndpointId == null)
            {
                result.Warnings.Add($"Rule '{exp.Name}' references unknown source endpoint '{exp.SourceEndpointRef}' - skipped");
                continue;
            }
            if (destEndpointId == null)
            {
                result.Warnings.Add($"Rule '{exp.Name}' references unknown destination endpoint '{exp.DestinationEndpointRef}' - skipped");
                continue;
            }

            // Parse operation
            if (!Enum.TryParse<Rule.RuleOperation>(exp.Operation, true, out var operation))
            {
                result.Warnings.Add($"Rule '{exp.Name}' has invalid operation '{exp.Operation}', defaulting to Copy");
                operation = Rule.RuleOperation.Copy;
            }

            // Parse TimeSpans
            TimeSpan.TryParse(exp.MaxDestinationAge, out var maxDestAge);
            TimeSpan.TryParse(exp.MinRetryTime, out var minRetry);
            TimeSpan.TryParse(exp.MaxTimeAfterSourceModification, out var maxTimeAfterMod);
            TimeSpan.TryParse(exp.DailyTriggerTime, out var dailyTrigger);

            var existing = await _context.Rules
                .FirstOrDefaultAsync(r => r.UserId == _currentUser.Id && r.Name == exp.Name, cancellationToken);

            if (existing != null)
            {
                if (strategy == MergeStrategy.SkipExisting)
                {
                    result.RulesSkipped++;
                    continue;
                }

                // Update existing
                existing.Comment = exp.Comment ?? "";
                existing.SourceEndpointId = sourceEndpointId.Value;
                existing.DestinationEndpointId = destEndpointId.Value;
                existing.Operation = operation;
                existing.MaxDestinationAge = maxDestAge;
                existing.MinRetryTime = minRetry;
                existing.MaxTimeAfterSourceModification = maxTimeAfterMod;
                existing.DailyTriggerTime = dailyTrigger;
                result.RulesUpdated++;
            }
            else
            {
                // Create new
                var rule = new Rule
                {
                    UserId = _currentUser.Id,
                    Name = exp.Name,
                    Comment = exp.Comment ?? "",
                    SourceEndpointId = sourceEndpointId.Value,
                    DestinationEndpointId = destEndpointId.Value,
                    Operation = operation,
                    MaxDestinationAge = maxDestAge,
                    MinRetryTime = minRetry,
                    MaxTimeAfterSourceModification = maxTimeAfterMod,
                    DailyTriggerTime = dailyTrigger
                };

                _context.Rules.Add(rule);
                result.RulesCreated++;
            }
        }
    }

    private async Task<int?> ResolveEndpointIdAsync(
        string endpointRef,
        Dictionary<string, int> idMap,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(endpointRef))
            return null;

        if (idMap.TryGetValue(endpointRef, out var id))
            return id;

        // Try to find existing endpoint by name
        var existing = await _context.Endpoints
            .FirstOrDefaultAsync(e => e.UserId == _currentUser.Id && e.Name == endpointRef, cancellationToken);

        return existing?.Id;
    }

    /// <summary>
    /// Export configuration to a file
    /// </summary>
    public async Task ExportConfigToFileAsync(
        string filePath,
        bool includePasswords = false,
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var json = await ExportConfigToJsonAsync(includePasswords, includeInactive, cancellationToken);
        await File.WriteAllTextAsync(filePath, json, cancellationToken);
    }

    /// <summary>
    /// Import configuration from a file
    /// </summary>
    public async Task<ImportResult> ImportConfigFromFileAsync(
        string filePath,
        MergeStrategy strategy = MergeStrategy.SkipExisting,
        CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(filePath, cancellationToken);
        return await ImportConfigAsync(json, strategy, cancellationToken);
    }
}
