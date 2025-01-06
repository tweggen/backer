using Hannibal.Configuration;
using Hannibal.Data;
using Hannibal.Services;
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
        services.Configure<HannibalServiceOptions>(
            configuration.GetSection("HannibalService"));

        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Backer",
            "hannibal.db"
        );
        
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath));
        var connectionString = $"Data Source={dbPath}";

        services.AddDbContext<HannibalContext>(options =>
            options.UseSqlite(
                //configuration.GetConnectionString("HannibalDatabase")
                connectionString
            ));

        services.AddScoped<IHannibalService, HannibalService>();
        
        return services;
    }
}