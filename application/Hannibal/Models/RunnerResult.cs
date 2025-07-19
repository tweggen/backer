using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Hannibal.Models;

public class RunnerResult
{
    public enum RunnerStatus
    {
        Stopped = 0,
        Running = 1
    };
    
    public RunnerStatus NewStatus { get; set; }
}