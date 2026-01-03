using System.Diagnostics;
using Hannibal.Configuration;
using Hannibal.Data;
using Hannibal.Models;
using Hannibal.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
            var connectionString = Environment.GetEnvironmentVariable("HANNIBAL_DB_CONNECTION");
            Console.WriteLine($"Trying to connect to database with string {connectionString}");

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                connectionString = "Host=localhost;Port=5432;Database=hannibal;Username=postgres;Password=admin";
            }

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
        
        services.AddScoped<IHannibalService, HannibalService>();
        
        return services;
    }
}