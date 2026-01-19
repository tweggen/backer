using Microsoft.Extensions.Configuration;
using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;


namespace Tools;

public class ConfigHelper<TOptions> where TOptions : class, new()
{

    private readonly string _configFilePath;
    public IConfigurationRoot Configuration { get; }

    private ILogger<ConfigHelper<TOptions>> _logger;
    
    public ConfigHelper(
        ILogger<ConfigHelper<TOptions>> logger,
        Func<IConfigurationBuilder, IConfigurationBuilder> conf,  
        string appName = "Backer")
    {
        _logger = logger;

        string configDirectory;
        if (EnvironmentDetector.IsInteractiveDev())
        {
            configDirectory = Directory.GetCurrentDirectory();
        }
        else
        {
            configDirectory = EnvironmentDetector.GetConfigDir(appName);
            EnvironmentDetector.EnsureDirectory(configDirectory, appName);
        }

        _logger.LogInformation($"Using configDirectory {configDirectory},");

        _configFilePath = Path.Combine(configDirectory, "config.json");
        
        var envName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                      ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") 
                      ?? "Production"; 
        
        var machine = GetMachineName();

        _logger.LogInformation($"envName = {envName}, machine = {machine}");
        
        var builder = new ConfigurationBuilder()
            .SetBasePath(configDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile($"appsettings.{envName}.json", optional: true, reloadOnChange: true) 
            .AddJsonFile($"appsettings.{envName}.{machine}.json", optional: true, reloadOnChange: true)            
            .AddJsonFile("config.json", optional: true, reloadOnChange: true);
        builder = conf(builder);
        builder = builder
            .AddEnvironmentVariables()
            .AddCommandLine(Environment.GetCommandLineArgs());

        Configuration = builder.Build();
        _logger.LogInformation(Configuration.GetDebugView());
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
        var sectionName = typeof(TOptions).Name;
        if (sectionName.EndsWith("Options", StringComparison.OrdinalIgnoreCase))
        {
            sectionName = sectionName.Substring(0, sectionName.Length - "Options".Length);
        }

        // Wrap the options under the section
        var wrapper = new Dictionary<string, TOptions>
        {
            { sectionName, options }
        };

        var json = JsonSerializer.Serialize(wrapper,
            new JsonSerializerOptions { WriteIndented = true });

        // Write atomically: temp file + replace
        var tempFile = _configFilePath + ".tmp";
        File.WriteAllText(tempFile, json);
        File.Move(tempFile, _configFilePath, overwrite: true);
    }

    /// <summary>
    /// Get the machine name, preferring LocalHostName on macOS since
    /// Environment.MachineName can return unexpected values (e.g., Android emulator IDs).
    /// </summary>
    private static string GetMachineName()
    {
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("scutil", "--get LocalHostName")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = System.Diagnostics.Process.Start(psi);
                if (process != null)
                {
                    var output = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit();
                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        return output;
                    }
                }
            }
            catch
            {
                // Fall through to default
            }
        }
        
        return Environment.MachineName;
    }
}