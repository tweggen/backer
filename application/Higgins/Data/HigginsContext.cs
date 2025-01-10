using Higgins.Data.Configurations;
using Higgins.Models;
using Microsoft.EntityFrameworkCore;

namespace Higgins.Data;

public class HigginsContext : DbContext
{
    public HigginsContext(DbContextOptions<HigginsContext> options)
        : base(options)
    {
    }
    
    public DbSet<Endpoint> Endpoints { get; set; }
    public DbSet<Route> Routes { get; set; }

    public async Task InitializeDatabaseAsync()
    {
        // This ensures the database is created
        await Database.EnsureCreatedAsync();
        
        // Optionally, you could seed initial data here
        if (!await Endpoints.AnyAsync())
        {
            // Add any required initial data
        }
        
        // Optionally, you could seed initial data here
        if (!await Routes.AnyAsync())
        {
            // Add any required initial data
        }
    }
    
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // modelBuilder.ApplyConfiguration<HigginsJobConfiguration>(new HigginsJobConfiguration());
    }
}