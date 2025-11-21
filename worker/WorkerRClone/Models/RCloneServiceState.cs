namespace WorkerRClone.Models;

public class RCloneServiceState
{
    public enum ServiceState {
        /**
         * This instance just has started. Do nothing until we found where
         * we are.
         */
        Starting,
        
        /**
         * We found there is no valid configuration.
         * So wait until we received a valid configuration.
         * A configuration is valid, if it passes basic checks.
         * A configuration can be invalidated by a problem logging in
         * or a path that proves to be wrong.
         *
         * If the first validation passes, the state progresses to WaitConfig.
         */
        WaitConfig,
        
        /**
         * We appear to have a valid configuration. So try to log in
         * online by calling something.
         * If that goes wrong, we return to WaitConfig invalidating the
         * current configuration.
         */
        CheckOnline,
        
        /**
         * We check if there is a running rclone instance fitting our
         * requirements. If there is, we transition to Running.
         */
        CheckRCloneProcess,
        
        /**
         * Most probably, we did not have a running rclone instance.
         * So try to start one. If this does not work, mark the configuration
         * invalid and transition to WaitConfig.
         * Transition to Running otherwise.
         */
        StartRCloneProcess,
        
        /**
         * Waiting to be started, if not activated
         */
        WaitStart,
        
        /**
         * We are checking for jobs, tryingf to execute them
         */
        Running,
        
        /**
         * We are waiting for the rclone service to stop operation.
         */
        WaitStop,
        
        /**
         * Exit has been requested.
         */
        Exiting
    }

    public RCloneServiceState.ServiceState State { get; set; } = ServiceState.Starting;
    public string StateString { get; set; } = "";
}