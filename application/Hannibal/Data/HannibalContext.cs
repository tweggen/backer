using Hannibal.Data.Configurations;
using Hannibal.Models;
using Hannibal.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore.Proxies;

namespace Hannibal.Data;

public class HannibalContext : IdentityDbContext
{
    private ILogger<HannibalContext> _logger;

    public HannibalContext(
        DbContextOptions<HannibalContext> options,
        ILogger<HannibalContext> logger)
        : base(options)
    {
        _logger = logger; 
    }
    
    
    public DbSet<Job> Jobs { get; set; }
    public DbSet<Rule> Rules { get; set; }
    public DbSet<RuleState> RuleStates { get; set; }
    public DbSet<Storage> Storages { get; set; }
    public DbSet<Endpoint> Endpoints { get; set; }
    public DbSet<OAuthState> OAuthStates { get; set; }



    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<OAuthState>(entity =>
        {
            entity.ToTable("oauth_state");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.UserId).IsRequired();
            entity.Property(x => x.Provider).IsRequired();
            entity.Property(x => x.ReturnUrl).IsRequired();
            entity.Property(x => x.CreatedAt).IsRequired();
        });
    }
    
    
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder
            .UseLazyLoadingProxies()
            ;
    }
}