using System.Diagnostics;
using Hannibal.Configuration;
using Hannibal.Data;
using Hannibal.Models;
using Hannibal.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Hannibal;

public static class DependencyInjection
{
    public static IServiceCollection AddHannibalService(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<OAuthOptions>( 
            configuration.GetSection("OAuth2"));
        
        services.Configure<HannibalServiceOptions>(
            configuration.GetSection("HannibalService"));

        var hannibalOptions = configuration
            .GetSection("HannibalService")
            .Get<HannibalServiceOptions>();
        
        var oauth2Options = configuration
            .GetSection("OAuth2")
            .Get<OAuthOptions>();
        
        try
        {
            /*
             * Connection string resolution order:
             *   1. ConnectionStrings:DefaultConnection (appsettings / in-memory test config)
             *   2. HANNIBAL_DB_CONNECTION environment variable (used by the live deployment)
             *   3. Hardcoded local development fallback
             */
            var connectionString = configuration.GetConnectionString("DefaultConnection");
            var connectionSource = "ConnectionStrings:DefaultConnection";

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                connectionString = Environment.GetEnvironmentVariable("HANNIBAL_DB_CONNECTION");
                connectionSource = "HANNIBAL_DB_CONNECTION environment variable";
            }

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                connectionString = "Host=localhost;Port=5432;Database=hannibal;Username=postgres;Password=admin";
                connectionSource = "built-in development default";
            }

            /*
             * Never log the connection string itself, it carries the database password.
             * Only the non-secret host/database coordinates are reported.
             */
            Console.WriteLine(
                $"Using database {_describeConnectionTarget(connectionString)} (from {connectionSource}).");

            services.AddDbContext<HannibalContext>(options =>
                options.UseNpgsql(connectionString));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Unable to create DB Context: {e}");
        }

        services.AddIdentityApiEndpoints<IdentityUser>(options => 
            {
                // Configure password requirements
                options.Password.RequireDigit = true;
                options.Password.RequiredLength = 6;
                options.Password.RequireNonAlphanumeric = false;
    
                // Configure lockout settings
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
                options.Lockout.MaxFailedAccessAttempts = 5;
            })
            .AddEntityFrameworkStores<HannibalContext>()
            .AddDefaultTokenProviders();
        
        services.Configure<IdentityOptions>(options =>
        {
            // Configure identity options here
        });
        
        /*
         * The OAuth2 client factory is stateless apart from the options snapshot it
         * creates its clients from, so a singleton is enough. It used to be created
         * inline inside the HannibalService constructor.
         */
        services.AddSingleton<IOAuth2ClientFactory>(sp =>
            new OAuth2ClientFactory(
                sp.GetRequiredService<IOptions<OAuthOptions>>().Value));

        services.AddScoped<IOAuthStateService, OAuthStateService>();
        services.AddScoped<IHannibalService, HannibalService>();

        return services;
    }


    /// <summary>
    /// Renders only the non-secret coordinates (host/port/database) of a connection
    /// string so that it is safe to write to stdout. The password is never included.
    /// </summary>
    private static string _describeConnectionTarget(string connectionString)
    {
        string? host = null;
        string? port = null;
        string? database = null;

        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = part.Substring(0, separator).Trim();
            var value = part.Substring(separator + 1).Trim();

            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Server", StringComparison.OrdinalIgnoreCase))
            {
                host = value;
            }
            else if (key.Equals("Port", StringComparison.OrdinalIgnoreCase))
            {
                port = value;
            }
            else if (key.Equals("Database", StringComparison.OrdinalIgnoreCase))
            {
                database = value;
            }
        }

        return $"{host ?? "<unknown host>"}:{port ?? "5432"}/{database ?? "<unknown database>"}";
    }
}