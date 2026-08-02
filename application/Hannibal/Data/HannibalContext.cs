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


    
    /// <summary>
    /// Total time we keep retrying to reach the database before giving up.
    /// Bounded on purpose: an unreachable or misconfigured database must fail
    /// with a clear exception instead of hanging the host forever.
    /// </summary>
    private static readonly TimeSpan _initializeTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan _initializeRetryDelay = TimeSpan.FromSeconds(5);


    public async Task InitializeDatabaseAsync()
    {
        bool haveDatabase = false;
        Exception? lastError = null;
        var deadline = DateTime.UtcNow + _initializeTimeout;
        var attempt = 0;

        while (!haveDatabase)
        {
            attempt++;
            try
            {
                await Database.EnsureCreatedAsync();
                haveDatabase = true;
                break;
            }
            catch (Exception e)
            {
                lastError = e;
                Console.Error.WriteLine($"Unable to create DB (attempt {attempt}): {e}");
            }

            if (DateTime.UtcNow + _initializeRetryDelay >= deadline)
            {
                throw new TimeoutException(
                    $"Unable to initialize the database after {attempt} attempt(s) within "
                    + $"{_initializeTimeout.TotalSeconds:F0}s. See inner exception for the last failure.",
                    lastError);
            }

            await Task.Delay(_initializeRetryDelay);
        }

        if (!!await Rules.AnyAsync())
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