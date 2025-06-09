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



    
    private async Task _ensureTestRule()
    {
        /*
         * If there is no rule, we create a rule from endpoint timomp3 in dropbox
         * to endpoint timomp3 in onedrive that is supposed to be executed daily.
         */

        var rule = await Rules.FirstOrDefaultAsync(
            r => r.Name == "timomp3 to onedrive");
        if (rule == null)
        {
            List<Rule> listRules = new();
            
            List<Rule> listSyncShares = new()
            {
                #if false
                new()
                {
                    Name = "timomp3 to onedrive",
                    User = await Users.FirstAsync(u => u.Username=="timo"),
                    SourceEndpoint = await Endpoints.FirstAsync(e => e.Name == "timo:dropbox:timomp3"),
                    DestinationEndpoint = await Endpoints.FirstAsync(e => e.Name == "timo:onedrive:timomp3"),
                    Operation = Rule.RuleOperation.Copy,
                    MaxDestinationAge = new TimeSpan(24, 0, 0),
                    MaxTimeAfterSourceModification = TimeSpan.MaxValue,
                    DailyTriggerTime = new(0,0,0,2, 0, 0)
                },
                new()
                {
                    Name = "prof to onedrive",
                    User = await Users.FirstAsync(u => u.Username=="timo"),
                    SourceEndpoint = await Endpoints.FirstAsync(e => e.Name == "timo:dropbox:prof"),
                    DestinationEndpoint = await Endpoints.FirstAsync(e => e.Name == "timo:onedrive:prof"),
                    Operation = Rule.RuleOperation.Copy,
                    MaxDestinationAge = new TimeSpan(24, 0, 0),
                    MaxTimeAfterSourceModification = TimeSpan.MaxValue,
                    DailyTriggerTime = new(0,0,0,2, 0, 0)
                },
                new()
                {
                    Name = "nassau to onedrive",
                    User = await Users.FirstAsync(u => u.Username=="timo"),
                    SourceEndpoint = await Endpoints.FirstAsync(e => e.Name == "timo:dropbox:nassau"),
                    DestinationEndpoint = await Endpoints.FirstAsync(e => e.Name == "timo:onedrive:nassau"),
                    Operation = Rule.RuleOperation.Copy,
                    MaxDestinationAge = new TimeSpan(24, 0, 0),
                    MaxTimeAfterSourceModification = TimeSpan.MaxValue,
                    DailyTriggerTime = new(0,0,0,2, 0, 0)
                },
                new()
                {
                    Name = "books to onedrive",
                    User = await Users.FirstAsync(u => u.Username=="timo"),
                    SourceEndpoint = await Endpoints.FirstAsync(e => e.Name == "timo:dropbox:books"),
                    DestinationEndpoint = await Endpoints.FirstAsync(e => e.Name == "timo:onedrive:books"),
                    Operation = Rule.RuleOperation.Copy,
                    MaxDestinationAge = new TimeSpan(24, 0, 0),
                    MaxTimeAfterSourceModification = TimeSpan.MaxValue,
                    DailyTriggerTime = new(0,0,0,2, 0, 0)
                },
                new()
                {
                    Name = "zeug to dropbox",
                    User = await Users.FirstAsync(u => u.Username=="timo"),
                    SourceEndpoint = await Endpoints.FirstAsync(e => e.Name == "timo:onedrive:zeug"),
                    DestinationEndpoint = await Endpoints.FirstAsync(e => e.Name == "timo:dropbox:zeug"),
                    Operation = Rule.RuleOperation.Copy,
                    MaxDestinationAge = new TimeSpan(24, 0, 0),
                    MaxTimeAfterSourceModification = TimeSpan.MaxValue,
                    DailyTriggerTime = new(0,0,0,2, 0, 0)
                },
#endif
            };

            foreach (var r in listSyncShares)
            {
                await Rules.AddAsync(r);
            }

            
            #if false
            Rule ruleMirrorOnedrive = new()
            {
                Name = "onedrive to rodrigo",
                Username = "timo",
                DependsOn = new List<string>(listSyncShares.Select(r => r.Name)),
                SourceEndpoint = "timo:onedrive:all",
                DestinationEndpoint = "timo:rodrigo:onedrive_bak",
                Operation = Rule.RuleOperation.Sync,
                MaxDestinationAge = new (24,0,0),
                MaxTimeAfterSourceModification = TimeSpan.MaxValue,
                DailyTriggerTime = new (2,0,0)
            };

            await Rules.AddAsync(ruleMirrorOnedrive);
            #endif
            

            await SaveChangesAsync();
        }
    }


    public async Task InitializeDatabaseAsync()
    {
        await Database.EnsureCreatedAsync();
        
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
            IsActive = true
        };
        Storage timosOnedrive = new()
        {
            UserId = userTimo,
            //Credentials = timosOnedriveCredentials,
            Technology = "onedrive",
            UriSchema = "TimosOnedrive",
            IsActive = true
        };
        Storage timosRodrigo = new()
        {
            UserId = userTimo,
            Technology = "smb",
            UriSchema = "TimosRodrigo",
            IsActive = true
        };

        List<Endpoint> listEndpoints = new()
        {
            new(userTimo, timosRodrigo, "/public", "original public media") { IsActive = true },
            new (userTimo, timosOnedrive, "public", "onedrive public media") { IsActive = true }
#if false
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
#endif
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