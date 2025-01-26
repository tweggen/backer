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

            Rule ruleTimomp3ToOnedrive = new()
            {
                Name = "timomp3 to onedrive",
                SourceEndpoint = "timo:dropbox:timomp3",
                DestinationEndpoint = "timo:onedrive:timomp3",
                Operation = Rule.RuleOperation.Nop,
                MaxDestinationAge = new TimeSpan(24, 0, 0),
                MaxTimeAfterSourceModification = TimeSpan.MaxValue,
                DailyTriggerTime = new(2, 0, 0)
            };

            await Rules.AddAsync(ruleTimomp3ToOnedrive);
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