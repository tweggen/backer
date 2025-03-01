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
    public DbSet<Rule> Rules { get; set; }
    public DbSet<RuleState> RuleStates { get; set; }

    
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
                new()
                {
                    Name = "timomp3 to onedrive",
                    Username = "timo",
                    SourceEndpoint = "timo:dropbox:timomp3",
                    DestinationEndpoint = "timo:onedrive:timomp3",
                    Operation = Rule.RuleOperation.Copy,
                    MaxDestinationAge = new TimeSpan(24, 0, 0),
                    MaxTimeAfterSourceModification = TimeSpan.MaxValue,
                    DailyTriggerTime = new(0,0,0,2, 0, 0)
                },
                new()
                {
                    Name = "prof to onedrive",
                    Username = "timo",
                    SourceEndpoint = "timo:dropbox:prof",
                    DestinationEndpoint = "timo:onedrive:prof",
                    Operation = Rule.RuleOperation.Copy,
                    MaxDestinationAge = new TimeSpan(24, 0, 0),
                    MaxTimeAfterSourceModification = TimeSpan.MaxValue,
                    DailyTriggerTime = new(0,0,0,2, 0, 0)
                },
                new()
                {
                    Name = "nassau to onedrive",
                    Username = "timo",
                    SourceEndpoint = "timo:dropbox:nassau",
                    DestinationEndpoint = "timo:onedrive:nassau",
                    Operation = Rule.RuleOperation.Copy,
                    MaxDestinationAge = new TimeSpan(24, 0, 0),
                    MaxTimeAfterSourceModification = TimeSpan.MaxValue,
                    DailyTriggerTime = new(0,0,0,2, 0, 0)
                },
                new()
                {
                    Name = "books to onedrive",
                    Username = "timo",
                    SourceEndpoint = "timo:dropbox:books",
                    DestinationEndpoint = "timo:onedrive:books",
                    Operation = Rule.RuleOperation.Copy,
                    MaxDestinationAge = new TimeSpan(24, 0, 0),
                    MaxTimeAfterSourceModification = TimeSpan.MaxValue,
                    DailyTriggerTime = new(0,0,0,2, 0, 0)
                },
                new()
                {
                    Name = "zeug to dropbox",
                    Username = "timo",
                    SourceEndpoint = "timo:onedrive:zeug",
                    DestinationEndpoint = "timo:dropbox:zeug",
                    Operation = Rule.RuleOperation.Copy,
                    MaxDestinationAge = new TimeSpan(24, 0, 0),
                    MaxTimeAfterSourceModification = TimeSpan.MaxValue,
                    DailyTriggerTime = new(0,0,0,2, 0, 0)
                },
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
        // This ensures the database is created
        await Database.EnsureCreatedAsync();
        
        /*
         * Let's insert some test data.
         */
        if (!await Rules.AnyAsync())
        {
            _ensureTestRule();
        }
    }
    
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {   
        // modelBuilder.ApplyConfiguration<HannibalJobConfiguration>(new HannibalJobConfiguration());
    }
}