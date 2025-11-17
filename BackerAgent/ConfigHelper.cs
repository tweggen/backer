using Microsoft.Extensions.Configuration;
using System;
using System.IO;
using System.Text.Json;


namespace BackerAgent;

public class ConfigHelper<TOptions> where TOptions : class, new()
{

    private readonly string _configFilePath;
    public IConfigurationRoot Configuration { get; }

    public ConfigHelper(string appName = "RCloneService")
    {
        var programDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            appName);

        _configFilePath = Path.Combine(programDataPath, "config.json");

        if (!Directory.Exists(programDataPath))
        {
            Directory.CreateDirectory(programDataPath);
        }

        var builder = new ConfigurationBuilder()
            .SetBasePath(programDataPath)
            .AddJsonFile("config.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .AddCommandLine(Environment.GetCommandLineArgs());

        Configuration = builder.Build();
    }

    /// <summary>
    /// Load the full options object from ProgramData JSON.
    /// </summary>
    public TOptions Load()
    {
        if (!File.Exists(_configFilePath))
            return new TOptions();

        var json = File.ReadAllText(_configFilePath);
        return JsonSerializer.Deserialize<TOptions>(json) ?? new TOptions();
    }

    /// <summary>
    /// Save the full options object atomically.
    /// </summary>
    public void Save(TOptions options)
    {
        var json = JsonSerializer.Serialize(options,
            new JsonSerializerOptions { WriteIndented = true });

        // Write atomically: temp file + replace
        var tempFile = _configFilePath + ".tmp";
        File.WriteAllText(tempFile, json);
        File.Move(tempFile, _configFilePath, overwrite: true);
    }
}}