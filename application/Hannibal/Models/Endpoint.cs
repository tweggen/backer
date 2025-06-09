using Microsoft.AspNetCore.Routing;

namespace Hannibal.Models;


/**
 * Describes an endpoint of a monitoring or transfer as used by
 * a specific user.
 */
public class Endpoint
{
    public Endpoint()
    {
        Comment = "";
    }
    
    internal Endpoint(string userId, Storage storage, string path, string? comment = null)
    {
        UserId = userId;
        Storage = storage;
        Path = path;
        Name = $"{userId}:{Storage.Technology}:{Path}";
        if (null != comment)
        {
            Comment = comment;
        }
        else
        {
            Comment = "";
        }
    }
    
    public int Id { get; set; }
    public string Name { get; set; }
    public string UserId { get; set; }
    public int StorageId { get; set; }
    public virtual Storage Storage { get; set; }
    public string Path { get; set; }
    
    public string Comment { get; set; }
    
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsActive { get; set; }
}