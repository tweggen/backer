using Hannibal.Models;
using Higgins.Data.Configurations;
using Higgins.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Higgins.Data;

public class HigginsContext : DbContext
{
    private ILogger<HigginsContext> _logger;
    
    public HigginsContext(DbContextOptions<HigginsContext> options, ILogger<HigginsContext> logger)
        : base(options)
    {
        _logger = logger;
    }
    
    public async Task InitializeDatabaseAsync()
    {
        // This ensures the database is created
        await Database.EnsureCreatedAsync();
        
        // Optionally, you could seed initial data here
        if (!await Users.AnyAsync())
        {
            await _createDevContent();
        }
    }
    
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // modelBuilder.ApplyConfiguration<HigginsJobConfiguration>(new HigginsJobConfiguration());
    }


    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder
            .UseLazyLoadingProxies()
            ;
    }
    
    
    public DbSet<Storage> Storages { get; set; }
    public DbSet<Endpoint> Endpoints { get; set; }
    public DbSet<User> Users { get; set; }

    
    private async Task _createDevContent()
    {
        User userTimo = new() { Username = "timo" };
        Credentials timosOnedriveCredentials = new()
        {
            // TXWTODO: We just use a locally configured rclone.
        };
        Credentials timosDropboxCredentials = new()
        {
            // TXWTODO: We just use a locally configured rclone.
        };
        Storage timosDropbox = new()
        {
            User = userTimo,
            //Credentials = timosDropboxCredentials,
            Technology = "dropbox",
            UriSchema = "TimosDropbox"
        };
        Storage timosOnedrive = new()
        {
            User = userTimo,
            //Credentials = timosOnedriveCredentials,
            Technology = "onedrive",
            UriSchema = "TimosOnedrive"
        };

        List<Endpoint> listEndpoints = new()
        {
            new(userTimo, timosDropbox, "timomp3", "original timomp3"),
            new(userTimo, timosOnedrive, "timomp3", "shared timomp3"),
            new(userTimo, timosDropbox, "zeug", "shared esat data"),
            new(userTimo, timosOnedrive, "zeug", "original esat data"),
            new (userTimo, timosDropbox, "prof", "original prof"),
            new (userTimo, timosOnedrive, "prof", "shared prof"),
            new(userTimo, timosDropbox, "nassau", "original nassau"),
            new(userTimo, timosOnedrive, "nassau", "shared nassau"),
            new(userTimo, timosDropbox, "books", "original books"),
            new(userTimo, timosOnedrive, "books", "shared books")
        };
        
        {
            _logger.LogInformation("Adding test routes.");
            
            await Users.AddAsync(userTimo);
            await Storages.AddAsync(timosDropbox);
            await Storages.AddAsync(timosOnedrive);

            foreach (var ep in listEndpoints)
            {
                await Endpoints.AddAsync(ep);
            }

            await SaveChangesAsync();
        }
        
    }
    
    
}