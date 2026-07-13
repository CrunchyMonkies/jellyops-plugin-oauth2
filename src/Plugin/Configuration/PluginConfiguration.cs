using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.OAuth2.Configuration;

/// <summary>
/// Plugin configuration for the Keycloak/OIDC single sign-on flow.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the OIDC issuer (Keycloak realm URL), e.g.
    /// <c>https://keycloak.example.com/realms/media</c>. The plugin appends
    /// <c>/.well-known/openid-configuration</c> to discover endpoints and JWKS.
    /// </summary>
    public string OidcIssuer { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the OAuth2 client id registered in Keycloak for Jellyfin.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the OAuth2 client secret (confidential client).
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the space-separated OAuth2 scopes requested. Must include <c>openid</c>.
    /// </summary>
    public string Scopes { get; set; } = "openid profile email";

    /// <summary>
    /// Gets or sets the id_token claim the username is derived from (e.g. <c>preferred_username</c>).
    /// </summary>
    public string UsernameClaim { get; set; } = "preferred_username";

    /// <summary>
    /// Gets or sets the claim carrying the user's roles, read from the OIDC UserInfo response.
    /// Supports a dotted path for nested claims; Keycloak exposes realm roles at
    /// <c>realm_access.roles</c> (the default). The value may be a string array or a single string.
    /// </summary>
    public string RoleClaim { get; set; } = "realm_access.roles";

    /// <summary>
    /// Gets or sets the roles permitted to sign in. When non-empty, a user must hold at least one of
    /// these (or an <see cref="AdminRoles"/> entry) to be provisioned/logged in — otherwise sign-in
    /// is refused. When empty, any user Keycloak authenticates is allowed.
    /// </summary>
    public string[] AllowedRoles { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the role values that grant Jellyfin administrator rights (any match wins).
    /// Holding an admin role also satisfies <see cref="AllowedRoles"/>.
    /// </summary>
    public string[] AdminRoles { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets a value indicating whether users authenticated by Keycloak but missing in
    /// Jellyfin are auto-created on first login. When false, unknown users are rejected.
    /// </summary>
    public bool EnableUserProvisioning { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the plugin re-applies the admin/role mapping to an
    /// existing user on every login (keeps Jellyfin admin state in sync with Keycloak roles).
    /// </summary>
    public bool SyncRolesOnLogin { get; set; } = true;

    /// <summary>
    /// Gets or sets the public origin of the Jellyfin server (scheme + host, no trailing slash),
    /// used to build the absolute OAuth2 redirect_uri when the request origin cannot be trusted
    /// (e.g. behind a gateway). Leave empty to derive it from the incoming request.
    /// </summary>
    public string PublicOrigin { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the operator has enabled gateway-level auto-login
    /// redirect. Informational only (the actual redirect lives in the gateway HTTPRoute); surfaced
    /// on the settings page so admins can see the expected behaviour.
    /// </summary>
    public bool AutoLoginEnabled { get; set; }
}
