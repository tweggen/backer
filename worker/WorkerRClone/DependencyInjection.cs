using Hannibal;
using Hannibal.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WorkerRClone.Configuration;
using WorkerRClone.Services;
using WorkerRClone.Services.Providers;
using WorkerRClone.Services.Providers.Local;
using WorkerRClone.Services.Providers.OAuth;

namespace WorkerRClone;

public static class DependencyInjection
{
    public static IServiceCollection AddRCloneService(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<RCloneServiceOptions>(
            configuration.GetSection("RCloneService"));

        // Register OAuth2ClientFactory with options from configuration
        services.AddSingleton<OAuth2ClientFactory>(sp =>
        {
            var options = sp.GetRequiredService<IOptionsMonitor<RCloneServiceOptions>>();
            var oauthOptions = options.CurrentValue.OAuth2 ?? new OAuthOptions();
            var factory = new OAuth2ClientFactory(oauthOptions);
            
            // Subscribe to options changes
            options.OnChange(updated =>
            {
                if (updated?.OAuth2 != null)
                {
                    factory.OnUpdateOptions(updated.OAuth2);
                }
            });
            
            return factory;
        });

        // Register storage providers
        services.AddStorageProviders();
        
        services.AddHostedService<RCloneService>();  
        
        return services;
    }

    /// <summary>
    /// Register all storage providers and the provider factory.
    /// To add a new provider:
    /// 1. Create the provider class implementing IStorageProvider
    /// 2. Add a registration line here
    /// </summary>
    public static IServiceCollection AddStorageProviders(this IServiceCollection services)
    {
        // OAuth-based providers
        services.AddSingleton<IStorageProvider, DropboxProvider>();
        services.AddSingleton<IStorageProvider, OneDriveProvider>();
        services.AddSingleton<IStorageProvider, GoogleDriveProvider>();
        
        // Credential-based / local providers
        services.AddSingleton<IStorageProvider, SmbProvider>();
        services.AddSingleton<IStorageProvider, LocalProvider>();
        
        // Provider factory - collects all registered IStorageProvider instances
        services.AddSingleton<IStorageProviderFactory, StorageProviderFactory>();
        
        // Main storage manager
        services.AddSingleton<RCloneStorages>();
        
        return services;
    }
}
