using System;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.OAuth2.Auth;
using Jellyfin.Plugin.OAuth2.Configuration;
using Jellyfin.Plugin.OAuth2.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OAuth2.Api;

/// <summary>
/// OAuth2/OIDC login endpoints for Keycloak single sign-on. Auto-discovered by the Jellyfin host
/// (any <see cref="ControllerBase"/> in a plugin assembly is registered). All actions are anonymous
/// because they run before a Jellyfin session exists.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("sso")]
public sealed class OidcController : ControllerBase
{
    private readonly OidcService _oidc;
    private readonly OidcStateStore _stateStore;
    private readonly IUserManager _userManager;
    private readonly ISessionManager _sessionManager;
    private readonly IAuthorizationContext _authContext;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<OidcController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcController"/> class.
    /// </summary>
    /// <param name="oidc">The OIDC protocol service.</param>
    /// <param name="stateStore">The in-flight authorization state store.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="authContext">The authorization context (used to detect existing sessions).</param>
    /// <param name="appHost">The server application host (for the server id/name).</param>
    /// <param name="logger">The logger.</param>
    public OidcController(
        OidcService oidc,
        OidcStateStore stateStore,
        IUserManager userManager,
        ISessionManager sessionManager,
        IAuthorizationContext authContext,
        IServerApplicationHost appHost,
        ILogger<OidcController> logger)
    {
        _oidc = oidc;
        _stateStore = stateStore;
        _userManager = userManager;
        _sessionManager = sessionManager;
        _authContext = authContext;
        _appHost = appHost;
        _logger = logger;
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Plugin not initialised.");

    /// <summary>
    /// Begins the Keycloak Authorization-Code flow (with PKCE) by redirecting the browser to the
    /// identity provider. If the caller already presents a valid Jellyfin session, bounces straight
    /// to the web client to avoid a redundant round-trip (and redirect loops with the gateway).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A redirect to the identity provider (or the web client).</returns>
    [HttpGet("authorize")]
    public async Task<IActionResult> Authorize(CancellationToken cancellationToken)
    {
        try
        {
            var existing = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
            if (existing.IsAuthenticated && existing.User is not null)
            {
                return Redirect("/web/");
            }
        }
        catch (Exception ex)
        {
            // A malformed/expired token must not block starting a fresh sign-in — fall through.
            _logger.LogDebug(ex, "Ignoring authorization probe failure on /sso/authorize.");
        }

        if (string.IsNullOrWhiteSpace(Config.OidcIssuer) || string.IsNullOrWhiteSpace(Config.ClientId))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Keycloak SSO is not configured.");
        }

        var state = OidcService.RandomToken();
        var codeVerifier = OidcService.RandomToken();
        var codeChallenge = OidcService.CodeChallengeS256(codeVerifier);
        var redirectUri = $"{GetOrigin()}/sso/callback";

        _stateStore.Add(state, codeVerifier, redirectUri);

        try
        {
            var url = await _oidc.BuildAuthorizeUrlAsync(redirectUri, state, codeChallenge, cancellationToken).ConfigureAwait(false);
            return Redirect(url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build the Keycloak authorization URL.");
            return StatusCode(StatusCodes.Status502BadGateway, "Could not reach the identity provider.");
        }
    }

    /// <summary>
    /// Completes the flow: exchanges the authorization code, provisions/looks up the Jellyfin user,
    /// mints a native session, and hands the token to the web client via a localStorage seed page.
    /// </summary>
    /// <param name="code">The authorization code.</param>
    /// <param name="state">The state value echoed by the identity provider.</param>
    /// <param name="error">An optional error code from the identity provider.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An HTML handoff page, or an error status.</returns>
    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(error))
        {
            _logger.LogWarning("Keycloak returned an error: {Error}", error);
            return StatusCode(StatusCodes.Status401Unauthorized, $"Sign-in failed: {error}");
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            return BadRequest("Missing code or state.");
        }

        if (!_stateStore.TryConsume(state, out var codeVerifier, out var redirectUri))
        {
            return BadRequest("Unknown or expired sign-in state.");
        }

        OidcIdentity identity;
        try
        {
            identity = await _oidc.ExchangeCodeAsync(code, redirectUri, codeVerifier, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OIDC code exchange or id_token validation failed.");
            return StatusCode(StatusCodes.Status401Unauthorized, "Sign-in could not be verified.");
        }

        User user;
        try
        {
            user = await ResolveUserAsync(identity).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to provision Jellyfin user for {Username}.", identity.Username);
            return StatusCode(StatusCodes.Status403Forbidden, "This account is not permitted to sign in.");
        }

        var authResult = await _sessionManager.AuthenticateDirect(new AuthenticationRequest
        {
            UserId = user.Id,
            App = "Jellyfin Web",
            AppVersion = "SSO",
            DeviceId = "keycloak-sso-" + OidcService.RandomToken(),
            DeviceName = "Keycloak SSO",
            RemoteEndPoint = HttpContext.Connection.RemoteIpAddress?.ToString(),
        }).ConfigureAwait(false);

        _logger.LogInformation("Keycloak SSO minted a session for {Username}.", identity.Username);
        return Content(BuildHandoffHtml(authResult.AccessToken, authResult.User.Id, authResult.ServerId), "text/html", Encoding.UTF8);
    }

    private async Task<User> ResolveUserAsync(OidcIdentity identity)
    {
        var config = Config;
        var isAdmin = identity.Roles.Any(r => config.AdminRoles.Contains(r, StringComparer.OrdinalIgnoreCase));

        // Login gate: when AllowedRoles is configured, the user must hold an allowed role (an admin
        // role also counts). Denied users are never provisioned. Empty AllowedRoles = allow anyone.
        var allowed = config.AllowedRoles.Length == 0
            || isAdmin
            || identity.Roles.Any(r => config.AllowedRoles.Contains(r, StringComparer.OrdinalIgnoreCase));
        if (!allowed)
        {
            throw new InvalidOperationException(
                $"User '{identity.Username}' holds none of the required roles [{string.Join(", ", config.AllowedRoles)}].");
        }

        var user = _userManager.GetUserByName(identity.Username);
        if (user is null)
        {
            if (!config.EnableUserProvisioning)
            {
                throw new InvalidOperationException($"User '{identity.Username}' does not exist and provisioning is disabled.");
            }

            user = await _userManager.CreateUserAsync(identity.Username).ConfigureAwait(false);

            // Stamp new users as owned by this provider so local password login is blocked, and apply
            // the initial admin mapping. Only new users are claimed — pre-existing local accounts keep
            // their provider to avoid unexpectedly locking anyone out of password login.
            var policy = _userManager.GetUserDto(user).Policy;
            policy.AuthenticationProviderId = typeof(OidcAuthenticationProvider).FullName;
            policy.IsAdministrator = isAdmin;
            policy.EnableAllFolders = true;
            await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
        }
        else if (config.SyncRolesOnLogin)
        {
            var policy = _userManager.GetUserDto(user).Policy;
            if (policy.IsAdministrator != isAdmin)
            {
                policy.IsAdministrator = isAdmin;
                await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
            }
        }

        return _userManager.GetUserById(user.Id) ?? user;
    }

    // Prefers the configured public origin, else derives from forwarded/request headers.
    private string GetOrigin()
    {
        if (!string.IsNullOrWhiteSpace(Config.PublicOrigin))
        {
            return Config.PublicOrigin.TrimEnd('/');
        }

        var scheme = Request.Headers.TryGetValue("X-Forwarded-Proto", out var proto) && proto.Count > 0
            ? proto[0]
            : Request.Scheme;
        var host = Request.Headers.TryGetValue("X-Forwarded-Host", out var fwdHost) && fwdHost.Count > 0
            ? fwdHost[0]
            : Request.Host.Value;
        return $"{scheme}://{host}";
    }

    // Emits a minimal page that seeds the web client's credential store (localStorage
    // "jellyfin_credentials") with the freshly minted session, then navigates to the web client,
    // which auto-connects with the stored token. Mirrors jellyfin-apiclient's server schema.
    private string BuildHandoffHtml(string accessToken, Guid userId, string serverId)
    {
        var payload = JsonSerializer.Serialize(
            new
            {
                id = serverId,
                token = accessToken,
                userId = userId.ToString("N"),
                origin = GetOrigin(),
                name = _appHost.FriendlyName,
            },
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        return $$"""
<!DOCTYPE html>
<html lang="en">
<head><meta charset="utf-8"><title>Signing in…</title></head>
<body>
<p>Signing in…</p>
<script>
(function () {
    var d = {{payload}};
    var KEY = 'jellyfin_credentials';
    var creds;
    try { creds = JSON.parse(localStorage.getItem(KEY) || '{}'); } catch (e) { creds = {}; }
    if (!creds.Servers) { creds.Servers = []; }
    var server = creds.Servers.filter(function (s) {
        return s.Id && d.id && s.Id.toLowerCase() === d.id.toLowerCase();
    })[0];
    if (!server) { server = {}; creds.Servers.push(server); }
    server.Id = d.id;
    server.AccessToken = d.token;
    server.UserId = d.userId;
    server.ManualAddress = d.origin;
    server.LastConnectionMode = 2; // ConnectionMode.Manual

    server.Name = d.name;
    server.DateLastAccessed = (new Date()).getTime();
    localStorage.setItem(KEY, JSON.stringify(creds));
    window.location.replace('/web/');
})();
</script>
</body>
</html>
""";
    }
}
