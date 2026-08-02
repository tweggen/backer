using Xunit;

namespace WorkerRClone.Tests.TestSupport;

/// <summary>
/// A <see cref="FactAttribute"/> that opts a test out unless the live OneDrive
/// credential check has been explicitly enabled.
/// <para>
/// xUnit v2 has no built-in dynamic skip, so instead of pulling in
/// <c>Xunit.SkippableFact</c> this attribute evaluates the environment at test
/// <em>discovery</em> time and sets <see cref="FactAttribute.Skip"/>. A skipped test
/// never runs, so with the gate off there is provably not a single network call.
/// </para>
/// <para>
/// Enable with:
/// <code>
/// $env:BACKER_LIVE_OAUTH_TEST = "1"
/// $env:BACKER_LIVE_STORAGE_ID = "&lt;id of the onedrive Storage row&gt;"
/// $env:BACKER_LIVE_DB_CONNECTION = "Host=...;Database=hannibal;Username=...;Password=..."
/// </code>
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class LiveOAuthFactAttribute : FactAttribute
{
    public const string EnableVariable = "BACKER_LIVE_OAUTH_TEST";
    public const string StorageIdVariable = "BACKER_LIVE_STORAGE_ID";
    public const string ConnectionVariable = "BACKER_LIVE_DB_CONNECTION";
    public const string FallbackConnectionVariable = "HANNIBAL_DB_CONNECTION";

    public LiveOAuthFactAttribute()
    {
        Skip = ComputeSkipReason();
    }

    /// <summary>
    /// Null when the live check is fully configured, otherwise the reason it is skipped.
    /// </summary>
    public static string? ComputeSkipReason()
    {
        if (Get(EnableVariable) != "1")
        {
            return $"Live OneDrive credential check is opt-in and disabled. " +
                   $"Set {EnableVariable}=1, {StorageIdVariable}=<storage id> and " +
                   $"{ConnectionVariable}=<postgres connection string> to enable it. " +
                   "It performs a read-only GET https://graph.microsoft.com/v1.0/me/drive; " +
                   "it never starts rclone and never writes to the remote.";
        }

        var storageId = Get(StorageIdVariable);
        if (string.IsNullOrWhiteSpace(storageId))
        {
            return $"{EnableVariable}=1 but {StorageIdVariable} is not set - " +
                   "it must contain the Id of the onedrive Storage row to check.";
        }

        if (!int.TryParse(storageId, out _))
        {
            return $"{StorageIdVariable}='{storageId}' is not a valid integer storage id.";
        }

        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return $"{EnableVariable}=1 but neither {ConnectionVariable} nor " +
                   $"{FallbackConnectionVariable} is set - the live check reads the Storage row " +
                   "from the database and cannot run without a connection string.";
        }

        return null;
    }

    public static int StorageId => int.Parse(Get(StorageIdVariable)!);

    public static string? ConnectionString =>
        Get(ConnectionVariable) ?? Get(FallbackConnectionVariable);

    private static string? Get(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
