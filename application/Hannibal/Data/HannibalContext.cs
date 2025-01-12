using Hannibal.Data.Configurations;
using Hannibal.Models;
using Microsoft.EntityFrameworkCore;

namespace Hannibal.Data;

public class HannibalContext : DbContext
{
    public HannibalContext(DbContextOptions<HannibalContext> options)
        : base(options)
    {
    }
    
    
    public DbSet<Job> Jobs { get; set; }


    public async Task InitializeDatabaseAsync()
    {
        // This ensures the database is created
        await Database.EnsureCreatedAsync();
        
        // Optionally, you could seed initial data here
        if (!await Jobs.AnyAsync())
        {
            Jobs.AddAsync(new Job
            {
                Owner = "",
                State = 0,
                FromUri = "file:///tmp/a",
                ToUri = "onedrive/bak/",
            });
            await Jobs.AddAsync(new Job
            {
                Owner = "",
                State = 0,
                FromUri = "file:///tmp/b",
                ToUri = "onedrive/bak/",
            });
            await SaveChangesAsync();   
        }
    }
    
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {   
        // modelBuilder.ApplyConfiguration<HannibalJobConfiguration>(new HannibalJobConfiguration());
    }
}