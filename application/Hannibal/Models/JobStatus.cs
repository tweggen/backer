namespace Hannibal.Models;

public class JobStatus
{
    /**
     * The id of the job as defined by hannibal
     */
    public int JobId { get; set; }

    /**
     * The textual id of the owner who promised to execute the job.
     */
    public string Owner { get; set; }
    
    /**
     * The status that has been reported.
     */
    public Job.JobState State { get; set; }

    /**
     * Gate E AC8 (plan-git-repo-storage.md): when State is DoneFailure and
     * Terminal is true, the failure is recorded as-is (DoneFailure, Owner
     * cleared) instead of being requeued to Ready for an immediate retry -
     * so RuleScheduler schedules the rule's next job per
     * ScheduleCalculator's MinRetryTime path instead of the job spinning
     * back through acquisition seconds later. Set by an engine when a
     * tripped safety guard (not an ordinary transient failure) produced the
     * DoneFailure. Additive and wire-compatible: it defaults to false, so an
     * older agent that never sends it keeps today's retry-requeue behaviour
     * for every other JobState it is simply ignored.
     */
    public bool Terminal { get; set; } = false;
}