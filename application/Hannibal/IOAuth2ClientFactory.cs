using Hannibal.Configuration;
using OAuth2.Client;

namespace Hannibal;

/// <summary>
/// Creates the provider specific <see cref="OAuth2Client"/> instances.
/// Abstracted behind an interface so that consumers (HannibalService, the
/// WorkerRClone storage providers) can be tested without constructing real
/// OAuth2 clients bound to live provider endpoints.
/// </summary>
public interface IOAuth2ClientFactory
{
    /// <summary>
    /// Creates the OAuth2 client for the given provider key
    /// ("onedrive", "dropbox", "google"/"googledrive").
    /// </summary>
    /// <exception cref="KeyNotFoundException">
    /// Thrown if the provider is unknown or is not configured.
    /// </exception>
    OAuth2Client CreateOAuth2Client(Guid stateId, string provider);

    /// <summary>
    /// Replaces the options this factory creates its clients from.
    /// Used by the WorkerRClone options-change hook.
    /// </summary>
    void OnUpdateOptions(OAuthOptions? oauthOptions);
}
