﻿namespace Hannibal.Models;


/**
 * Describe a rule that shall shall generate regular jobs.
 * It is the job of hannibal to plan the jobs.
 */
public class Rule
{
    public int Id { get; set; }
    
    public string Name { get; set; }

    public string Comment { get; set; } = "";
    
    public string Username { get; set; }

    // TXWTODO: THese dependencies are not properly setup.
    //public ICollection<string> DependsOn { get; set; } = new List<string>();
 
    public string SourceEndpoint { get; set; }
    public string DestinationEndpoint { get; set; }
    
    /**
     * The operation that shall be executed.
     */
    public enum RuleOperation 
    {
        Nop,
        Copy,
        Sync
    }

    public RuleOperation Operation { get; set; }
    
    /**
     * What is the maximal age of the most recent object in the
     * destination before a new operation must be triggered?
     */
    public TimeSpan MaxDestinationAge { get; set; }
    
    /**
     * How long after the latest modification in the source
     * must an operation be triggered?
     */
    public TimeSpan MaxTimeAfterSourceModification { get; set; }
    
    /**
     * What is the preferred time on any day to start the operation
     * if there is no urgent indication listed.
     */
    public TimeSpan DailyTriggerTime { get; set; }
}