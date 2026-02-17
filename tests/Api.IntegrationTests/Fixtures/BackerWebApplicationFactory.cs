using Hannibal.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Api.IntegrationTests.Fixtures;

public class BackerWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = $"TestDb_{Guid.NewGuid()}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((context, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "ThisIsATestKeyThatIsAtLeast32BytesLongForHmacSha256!!",
                ["Jwt:Issuer"] = "test-issuer",
                ["Jwt:Audience"] = "test-audience",
                ["profiles:applicationUrl"] = "https://localhost:5001;http://localhost:5000",
            });
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Remove ALL EF Core DbContext-related registrations to fully
            // eliminate the Npgsql provider before adding InMemory
            var toRemove = services.Where(d =>
                d.ServiceType == typeof(DbContextOptions<HannibalContext>) ||
                d.ServiceType == typeof(DbContextOptions) ||
                d.ServiceType == typeof(HannibalContext)).ToList();
            foreach (var d in toRemove)
                services.Remove(d);

            // Register InMemory options directly (avoid AddDbContext TryAdd semantics)
            services.AddScoped<DbContextOptions<HannibalContext>>(sp =>
            {
                var optionsBuilder = new DbContextOptionsBuilder<HannibalContext>();
                optionsBuilder.UseInMemoryDatabase(_dbName);
                return optionsBuilder.Options;
            });

            services.AddScoped<DbContextOptions>(sp =>
                sp.GetRequiredService<DbContextOptions<HannibalContext>>());

            services.AddScoped<HannibalContext>(sp =>
            {
                var options = sp.GetRequiredService<DbContextOptions<HannibalContext>>();
                var logger = sp.GetRequiredService<ILogger<HannibalContext>>();
                return new HannibalContext(options, logger);
            });
        });

        return base.CreateHost(builder);
    }
}
