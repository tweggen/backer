using System.Net;

namespace WorkerRClone.Services.Providers.OAuth;

/// <summary>
/// Raised when a call to the Microsoft Graph API fails.
/// <para>
/// Derives from <see cref="HttpRequestException"/> (and carries the
/// <see cref="HttpRequestException.StatusCode"/>) so that existing
/// <c>catch (HttpRequestException e) when (e.StatusCode == ...)</c> filters keep
/// working, but the message is descriptive enough to tell an expired/rejected
/// OAuth token apart from a transient transport failure.
/// </para>
/// </summary>
public sealed class MicrosoftGraphException : HttpRequestException
{
    /// <summary>
    /// True when Microsoft Graph refused the supplied access token (HTTP 401/403).
    /// </summary>
    public bool IsTokenRejected { get; }

    public MicrosoftGraphException(string message, HttpStatusCode statusCode)
        : base(message, null, statusCode)
    {
        IsTokenRejected = statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
    }
}
