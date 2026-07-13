using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Model.Users;

namespace Jellyfin.Plugin.OAuth2.Auth;

/// <summary>
/// Authentication provider that owns Keycloak-provisioned users. Interactive password login is not
/// supported for these users — they authenticate out-of-band via the OIDC flow in
/// <see cref="Api.OidcController"/>, which mints their session directly. Stamping a provisioned
/// user's <see cref="User.AuthenticationProviderId"/> with this provider's type name routes any
/// subsequent password-login attempt here, where it is rejected.
/// </summary>
public sealed class OidcAuthenticationProvider : IAuthenticationProvider, IHasNewUserPolicy
{
    /// <inheritdoc />
    public string Name => "Keycloak";

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public Task<ProviderAuthenticationResult> Authenticate(string username, string password)
    {
        // Users provisioned by this provider must sign in through the Keycloak SSO flow, never with
        // a local password. Fail closed.
        throw new AuthenticationException("This account signs in with Keycloak single sign-on.");
    }

    /// <inheritdoc />
    public Task ChangePassword(User user, string newPassword)
    {
        // Passwords are managed in Keycloak, not Jellyfin.
        throw new AuthenticationException("Passwords for Keycloak accounts are managed in Keycloak.");
    }

    /// <inheritdoc />
    public UserPolicy GetNewUserPolicy() => new()
    {
        EnableAllFolders = true,
        EnableMediaPlayback = true,
        EnableRemoteAccess = true,
    };
}
