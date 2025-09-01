namespace Hannibal.Models;

public class RuleState
{
    public int Id { get; set; }
    
    
    public int RuleId { get; set; }
    
    /**
     * The state of which rule am I reflecting here?
     */
    public virtual Rule Rule { get; set; }
    
    // TXWTODO: allow inactive rules etc.
    
    /**
     * When do we need to reevaluate this rule. 
     */
    public DateTime ExpiredAfter { get; set; }

    
    /**
     * Which was the most recently triggered job?
     */
    public virtual Job RecentJob { get; set; }
}