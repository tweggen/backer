using Microsoft.AspNetCore.Mvc;

namespace Hannibal;


[ApiController]
[Route("[controller]")]
public class JobController : ControllerBase
{
    private static readonly List<Job> Jobs = new List<Job>
    {
        new Job { Id = 1, State = Job.JobState.Preparing, FromUri = "file:///tmp/a", ToUri = "file:///tmp/b" },
        new Job { Id = 2, State = Job.JobState.Preparing, FromUri = "file:///tmp/a", ToUri = "file:///tmp/b" },
        // Add more products here
    };

    [HttpGet("{id}")]
    public ActionResult<Job> Get(int id)
    {
        var job = Jobs.Find(product => product.Id == id);

        if (job == null)
        {
            return NotFound();
        }

        return job;
    }
}