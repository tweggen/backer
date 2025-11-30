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



    
    public async Task InitializeDatabaseAsync()
    {
        bool haveDatabase = false;
        while (!haveDatabase)
        {
            try
            {
                await Database.EnsureCreatedAsync();
                haveDatabase = true;
                break;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Unable to create DB: {e}");
            }

            Thread.Sleep(5000);
        }

        if (!await Rules.AnyAsync())
        {
            await _createDevContent();
        }

        if (!await Rules.AnyAsync())
        {
            // await _ensureTestRule();
        }
        
    }
    
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // modelBuilder.ApplyConfiguration<HannibalJobConfiguration>(new HannibalJobConfiguration());
    }
    
    
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder
            .UseLazyLoadingProxies()
            ;
    }

    private async Task _createDevContent()
    {
        if (await Storages.AnyAsync() || await Endpoints.AnyAsync())
        {
            /*
             * No need to create any data.
             */
            return;
        }
        
        string userTimo = "timo";

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
            UserId = userTimo,
            //Credentials = timosDropboxCredentials,
            Technology = "dropbox",
            UriSchema = "TimosDropbox",
            Networks = "",
            IsActive = true
        };
        Storage timosOnedrive = new()
        {
            UserId = userTimo,
            //Credentials = timosOnedriveCredentials,
            Technology = "onedrive",
            UriSchema = "TimosOnedrive",
            Networks = "",
            IsActive = true
        };
        Storage timosRodrigo = new()
        {
            UserId = userTimo,
            Technology = "smb",
            UriSchema = "TimosRodrigo",
            Networks = "fe80::e72:74ff:fe07:ee9c",
            IsActive = true
        };

        List<Endpoint> listEndpoints = new()
        {
            new(userTimo, timosRodrigo, "/public", "original public media") { IsActive = true },
            new (userTimo, timosOnedrive, "public", "onedrive public media") { IsActive = true }
        };
        
        {
            _logger.LogInformation("Adding test routes.");
            
            await Storages.AddAsync(timosDropbox);
            await Storages.AddAsync(timosOnedrive);
            await Storages.AddAsync(timosRodrigo);

            foreach (var ep in listEndpoints)
            {
                await Endpoints.AddAsync(ep);
            }

            await SaveChangesAsync();
        }
        
    }


}