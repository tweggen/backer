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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // modelBuilder.ApplyConfiguration<HigginsJobConfiguration>(new HigginsJobConfiguration());
    }
}