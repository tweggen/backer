namespace Hannibal.Models;

public class EndpointState
{
    /**
     * What endpoint are we referring to?
     */
    public string EndpointPath { get; set; } = "";


    /**
     * which user is using it?
     */
    public string Username { get; set; } = "";
    
    public enum AccessState
    {
        Idle,
        Reading,
        Writing
    }

    public AccessState State { get; set; } = AccessState.Idle;
}