namespace WorkerRClone.Client.Models;

public class JobStatusResult
{
    public float duration { get; set; }
    public string endTime { get; set; }
    public string error { get; set; }
    public bool finished { get; set; }
    public int id { get; set; }
    public string startTime { get; set; }
    public bool success { get; set; }
    // output

    public override string ToString()
    {
        return
            $"{{ duration: {duration}, endTime: {endTime}, error: {error}, finished: {finished}, id: {id}, startTime: {startTime}, success: {success} }}";
    }
}