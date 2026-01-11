namespace WorkerRClone.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public class RCloneConfigManager
{
    // In-memory representation:
    // Dictionary<remoteName, Dictionary<key, value>>
    private readonly Dictionary<string, Dictionary<string, string>> _remotes =
        new(StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------
    // Load config from file
    // ------------------------------------------------------------
    public void LoadFromFile(string path)
    {
        if (!File.Exists(path))
            return;

        string[] lines = File.ReadAllLines(path);
        string? currentSection = null;

        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();

            // Skip empty lines and comments
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                continue;

            // Section header: [remoteName]
            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                currentSection = line.Substring(1, line.Length - 2).Trim();
                if (!_remotes.ContainsKey(currentSection))
                    _remotes[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            // Key/value: key = value
            if (currentSection != null)
            {
                int eq = line.IndexOf('=');
                if (eq > 0)
                {
                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();
                    _remotes[currentSection][key] = value;
                }
            }
        }
    }

    // ------------------------------------------------------------
    // Save config to file (atomic write)
    // ------------------------------------------------------------
    public void SaveToFile(string path)
    {
        string tempPath = path + ".tmp";

        using (var writer = new StreamWriter(tempPath, false, Encoding.UTF8))
        {
            foreach (var remote in _remotes)
            {
                writer.WriteLine($"[{remote.Key}]");
                foreach (var kv in remote.Value)
                {
                    writer.WriteLine($"{kv.Key} = {kv.Value}");
                }
                writer.WriteLine();
            }
        }

        // Atomic replace
        File.Copy(tempPath, path, overwrite: true);
        File.Delete(tempPath);
    }

    // ------------------------------------------------------------
    // Add or update a remote configuration
    // ------------------------------------------------------------
    public bool AddOrUpdateRemote(
        string remoteName,
        IDictionary<string, string> parameters)
    {
        bool changed = false;

        if (!_remotes.TryGetValue(remoteName, out var existing))
        {
            // Remote does not exist → everything is new
            _remotes[remoteName] = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase);
            return true;
        }

        // Compare and update keys
        foreach (var kv in parameters)
        {
            if (!existing.TryGetValue(kv.Key, out var currentValue))
            {
                // Missing key → add it
                existing[kv.Key] = kv.Value;
                changed = true;
            }
            else if (!string.Equals(currentValue, kv.Value, StringComparison.Ordinal))
            {
                // Value differs → update it
                existing[kv.Key] = kv.Value;
                changed = true;
            }
        }

        return changed;
    }

    // ------------------------------------------------------------
    // Retrieve a remote configuration
    // ------------------------------------------------------------
    public Dictionary<string, string>? GetRemote(string remoteName)
    {
        if (_remotes.TryGetValue(remoteName, out var config))
            return new Dictionary<string, string>(config, StringComparer.OrdinalIgnoreCase);

        return null;
    }
}
