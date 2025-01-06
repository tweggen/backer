namespace Hannibal.Models;

public class JobFilter
{
    public Job.JobState MinState { get; set; }
    public Job.JobState MaxState { get; set; }
}