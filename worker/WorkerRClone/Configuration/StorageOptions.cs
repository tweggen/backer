namespace WorkerRClone.Configuration;

public class StorageOptions
{
    public string Name { get; set; } = string.Empty;
    public SortedDictionary<string, string> Options = new();

    public StorageOptions(StorageOptions o)
    {
        Name = o.Name;
        Options = new SortedDictionary<string, string>(o.Options);
    }
}