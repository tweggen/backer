namespace Hannibal.Models;

public class RuleState
{
    public int Id { get; set; }
    
    /**
     * The state of which rule am I reflecting here?
     */
    public Rule Rule { get; set; }
    
    // TXWTODO: allow inactive rules etc.
    
    /**
     * When do we need to reevaluate this rule. 
     */
    public DateTime ExpiredAfter { get; set; }
}