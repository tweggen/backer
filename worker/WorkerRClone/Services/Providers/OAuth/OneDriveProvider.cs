using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Hannibal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WorkerRClone.Services.Providers.OAuth;

/// <summary>
/// Storage provider for Microsoft OneDrive
/// </summary>
public class OneDriveProvider : OAuthStorageProviderBase
{
    /// <summary>
    /// Name of the named <see cref="HttpClient"/> used for Microsoft Graph calls.
    /// Registered in <see cref="WorkerRClone.DependencyInjection.AddStorageProviders"/>.
    /// </summary>
    public const string GraphHttpClientName = "msgraph";

    /// <summary>
    /// Base address of the Microsoft Graph API.
    /// </summary>
    public static readonly Uri GraphBaseAddress = new("https://graph.microsoft.com/");

    /// <summary>
    /// Relative path of the read-only drive info endpoint.
    /// </summary>
    public const string DriveInfoPath = "v1.0/me/drive";

    /// <summary>
    /// Fallback client used when no <see cref="IHttpClientFactory"/> was injected
    /// (e.g. the provider was constructed outside of the regular DI container).
    /// Created lazily and shared - never per call.
    /// </summary>
    private static readonly Lazy<HttpClient> FallbackClient =
        new(() => new HttpClient { BaseAddress = GraphBaseAddress });

    private readonly IHttpClientFactory? _httpClientFactory;

    public override string Technology => "onedrive";

    public OneDriveProvider(
        ILogger<OneDriveProvider> logger,
        IOAuth2ClientFactory oauth2ClientFactory,
        IServiceScopeFactory serviceScopeFactory,
        IHttpClientFactory? httpClientFactory = null)
        : base(logger, oauth2ClientFactory, serviceScopeFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public override async Task<Dictionary<string, string>> BuildRCloneParametersAsync(
        StorageState state, CancellationToken cancellationToken)
    {
        var storage = state.Storage;

        if (string.IsNullOrWhiteSpace(storage.AccessToken))
        {
            return new Dictionary<string, string>();
        }

        var (driveId, driveType) = await GetDriveInfoAsync(storage.AccessToken, cancellationToken);

        var parameters = new Dictionary<string, string>
        {
            ["type"] = "onedrive",
            ["client_id"] = storage.ClientId,
            ["client_secret"] = storage.ClientSecret,
            ["auth_url"] = "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize",
            ["token_url"] = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
            ["drive_id"] = driveId,
            ["drive_type"] = driveType,
            ["token"] = BuildTokenJson(storage)
        };

        return parameters;
    }

    /// <summary>
    /// Get the drive ID and type from Microsoft Graph API.
    /// This is a read-only GET against <see cref="DriveInfoPath"/>; it never writes
    /// to the remote.
    /// </summary>
    /// <exception cref="MicrosoftGraphException">
    /// The Graph API answered with a non-success status code. For 401/403 the message
    /// states that the access token was rejected.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The Graph API answered 200 but the payload lacked a usable <c>id</c>/<c>driveType</c>.
    /// </exception>
    internal async Task<(string DriveId, string DriveType)> GetDriveInfoAsync(
        string accessToken, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory?.CreateClient(GraphHttpClientName) ?? FallbackClient.Value;

        // Build an absolute URI so the call works no matter whether the injected
        // client has a BaseAddress configured. Never mutate the (possibly shared)
        // client itself - the Authorization header goes onto the request.
        var requestUri = new Uri(client.BaseAddress ?? GraphBaseAddress, DriveInfoPath);

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseContentRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateGraphExceptionAsync(response, cancellationToken);
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Microsoft Graph returned a non-JSON body for '{DriveInfoPath}' (HTTP {(int)response.StatusCode}).",
                ex);
        }

        using (doc)
        {
            var driveId = ReadRequiredString(doc.RootElement, "id", "drive ID");
            var driveType = ReadRequiredString(doc.RootElement, "driveType", "drive type");

            return (driveId, driveType);
        }
    }

    /// <summary>
    /// Read a required non-empty string property, failing with a clear message.
    /// </summary>
    private static string ReadRequiredString(JsonElement root, string propertyName, string description)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(propertyName, out var element) ||
            element.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(element.GetString()))
        {
            throw new InvalidOperationException(
                $"{description} ('{propertyName}') not found in the Microsoft Graph response " +
                $"for '{DriveInfoPath}'. The OneDrive account may not have a drive provisioned.");
        }

        return element.GetString()!;
    }

    /// <summary>
    /// Turn a failed Graph response into a descriptive exception. The message names the
    /// status code and, for 401/403, states that the access token was rejected.
    /// The access token itself is never included.
    /// </summary>
    private static async Task<MicrosoftGraphException> CreateGraphExceptionAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = string.Empty;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            // The body is a diagnostic nicety - never let reading it mask the real failure.
        }

        if (body.Length > 512)
        {
            body = body[..512] + "... (truncated)";
        }

        var status = (int)response.StatusCode;

        string message;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            message =
                $"Microsoft Graph returned HTTP {status} ({response.StatusCode}) for '{DriveInfoPath}': " +
                "the access token was rejected by Microsoft Graph. The token is expired, revoked or " +
                "missing the required scope - the OneDrive storage needs to be re-authenticated.";
        }
        else
        {
            message =
                $"Microsoft Graph returned HTTP {status} ({response.StatusCode}) for '{DriveInfoPath}': " +
                "unable to determine the OneDrive drive id/type.";
        }

        if (!string.IsNullOrWhiteSpace(body))
        {
            message += $" Response body: {body}";
        }

        return new MicrosoftGraphException(message, response.StatusCode);
    }
}
