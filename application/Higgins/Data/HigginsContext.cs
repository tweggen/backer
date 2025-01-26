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
            UriSchema = "onedrive"
        };
        Storage timosOnedrive = new()
        {
            User = userTimo,
            //Credentials = timosOnedriveCredentials,
            Technology = "onedrive",
            UriSchema = "onedrive"
        };
        Endpoint dropboxTimomp3 = new()
        {
            User = userTimo,
            Storage = timosDropbox,
            Name = "timo:dropbox:timomp3",
            Path = "timomp3",
            Comment = "Originals of timo music"
        };
        Endpoint onedriveTimomp3 = new()
        {
            User = userTimo,
            Storage = timosOnedrive,
            Name = "timo:onedrive:timomp3",
            Path = "timomp3",
            Comment = "Shared copy of timo music"
        };

        Endpoint dropboxZeug = new()
        {
            User = userTimo,
            Storage = timosDropbox,
            Name = "timo:dropbox:zeug",
            Path = "zeug",
            Comment = "shared copy of esat data"
        };
        Endpoint onedriveZeug = new()
        {
            User = userTimo,
            Storage = timosOnedrive,
            Name = "timo:onedrive:zeug",
            Path = "zeug",
            Comment = "original esat data"
        };


        {
            _logger.LogInformation("Adding test routes.");

            await Users.AddAsync(userTimo);
            await Storages.AddAsync(timosDropbox);
            await Storages.AddAsync(timosOnedrive);
            await Endpoints.AddAsync(dropboxTimomp3);
            await Endpoints.AddAsync(onedriveTimomp3);
            await Endpoints.AddAsync(dropboxZeug);
            await Endpoints.AddAsync(onedriveZeug);

            await SaveChangesAsync();
        }
        
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
}