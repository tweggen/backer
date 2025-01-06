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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // modelBuilder.ApplyConfiguration<HannibalJobConfiguration>(new HannibalJobConfiguration());
    }
}