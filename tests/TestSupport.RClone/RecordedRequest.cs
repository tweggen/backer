using System.Text.Json;

namespace TestSupport.RClone;

/// <summary>
/// One request the stub received, kept so tests can assert on what the agent
/// asked rclone to do rather than on side effects.
/// </summary>
public sealed class RecordedRequest
{
    public RecordedRequest(string path, string body, DateTimeOffset receivedAt)
    {
        Path = path;
        Body = body;
        ReceivedAt = receivedAt;

        try
        {
            Json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body).RootElement.Clone();
        }
        catch (JsonException)
        {
            /*
             * A malformed body is itself worth asserting on, so it is recorded
             * rather than thrown away. Json stays at its default and callers
             * fall back to Body.
             */
        }
    }

    /// <summary>The RC path, e.g. <c>/sync/copy</c>.</summary>
    public string Path { get; }

    /// <summary>The raw request body exactly as it arrived.</summary>
    public string Body { get; }

    public DateTimeOffset ReceivedAt { get; }

    /// <summary>The parsed body, or a default element if the body was not JSON.</summary>
    public JsonElement Json { get; }

    /// <summary>
    /// Reads a top-level string property, returning null when it is absent.
    /// </summary>
    public string? GetString(string propertyName) =>
        Json.ValueKind == JsonValueKind.Object
        && Json.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public override string ToString() => $"{Path} {Body}";
}
