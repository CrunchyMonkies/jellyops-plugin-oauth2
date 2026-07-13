using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OAuth2.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Jellyfin.Plugin.OAuth2.Services;

/// <summary>
/// The result of a completed OIDC login: the resolved Jellyfin username and the roles/groups the
/// identity provider asserted for the user.
/// </summary>
/// <param name="Username">The username derived from the configured username claim.</param>
/// <param name="Roles">The role/group values from the configured role claim.</param>
public readonly record struct OidcIdentity(string Username, IReadOnlyList<string> Roles);

/// <summary>
/// Encapsulates the OpenID Connect protocol against the configured Keycloak realm: discovery,
/// authorize-URL construction (with PKCE), authorization-code exchange, and id_token validation.
/// </summary>
public sealed class OidcService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly object _configLock = new();
    private ConfigurationManager<OpenIdConnectConfiguration>? _configManager;
    private string? _configuredIssuer;

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Factory for the outbound HTTP client.</param>
    public OidcService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Plugin not initialised.");

    /// <summary>
    /// Generates a cryptographically random URL-safe token (used for state and PKCE verifiers).
    /// </summary>
    /// <returns>A base64url string with 256 bits of entropy.</returns>
    public static string RandomToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    /// <summary>
    /// Computes the PKCE S256 code challenge for a verifier.
    /// </summary>
    /// <param name="codeVerifier">The PKCE code verifier.</param>
    /// <returns>The S256 code challenge.</returns>
    public static string CodeChallengeS256(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64Url(hash);
    }

    /// <summary>
    /// Builds the identity-provider authorization URL to redirect the browser to.
    /// </summary>
    /// <param name="redirectUri">The callback URL the IdP redirects back to.</param>
    /// <param name="state">The opaque anti-CSRF state value.</param>
    /// <param name="codeChallenge">The PKCE S256 code challenge.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The absolute authorization URL.</returns>
    public async Task<string> BuildAuthorizeUrlAsync(string redirectUri, string state, string codeChallenge, CancellationToken cancellationToken)
    {
        var config = await GetOpenIdConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = Config.ClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = string.IsNullOrWhiteSpace(Config.Scopes) ? "openid" : Config.Scopes,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        };
        var qs = string.Join('&', query.Where(kv => kv.Value is not null)
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));
        var separator = config.AuthorizationEndpoint.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{config.AuthorizationEndpoint}{separator}{qs}";
    }

    /// <summary>
    /// Exchanges an authorization code for tokens and validates the returned id_token, returning the
    /// resolved user identity.
    /// </summary>
    /// <param name="code">The authorization code from the callback.</param>
    /// <param name="redirectUri">The redirect_uri used for the original request (must match).</param>
    /// <param name="codeVerifier">The PKCE code verifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The validated <see cref="OidcIdentity"/>.</returns>
    public async Task<OidcIdentity> ExchangeCodeAsync(string code, string redirectUri, string codeVerifier, CancellationToken cancellationToken)
    {
        var config = await GetOpenIdConfigurationAsync(cancellationToken).ConfigureAwait(false);

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = Config.ClientId,
            ["client_secret"] = Config.ClientSecret,
            ["code_verifier"] = codeVerifier,
        });

        var httpClient = _httpClientFactory.CreateClient(nameof(OidcService));
        using var response = await httpClient.PostAsync(new Uri(config.TokenEndpoint), content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Token endpoint returned {(int)response.StatusCode}: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("id_token", out var idTokenElement))
        {
            throw new InvalidOperationException("Token response did not contain an id_token.");
        }

        var idToken = idTokenElement.GetString() ?? throw new InvalidOperationException("id_token was null.");
        var accessToken = doc.RootElement.TryGetProperty("access_token", out var atElement) ? atElement.GetString() : null;

        // The id_token proves authentication and yields the username; roles are sourced from the
        // UserInfo endpoint (Keycloak reliably exposes realm_access.roles there when the roles
        // mapper has "Add to userinfo" enabled — see keycloak/realm-media.json).
        var username = await ValidateIdTokenUsernameAsync(idToken, config).ConfigureAwait(false);
        var roles = await FetchUserInfoRolesAsync(accessToken, config, cancellationToken).ConfigureAwait(false);
        return new OidcIdentity(username, roles);
    }

    private async Task<string> ValidateIdTokenUsernameAsync(string idToken, OpenIdConnectConfiguration config)
    {
        var handler = new JsonWebTokenHandler();
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = config.Issuer,
            ValidateAudience = true,
            ValidAudience = Config.ClientId,
            ValidateLifetime = true,
            IssuerSigningKeys = config.SigningKeys,
            ValidateIssuerSigningKey = true,
        };

        var result = await handler.ValidateTokenAsync(idToken, parameters).ConfigureAwait(false);
        if (!result.IsValid)
        {
            throw new InvalidOperationException("id_token validation failed.", result.Exception);
        }

        // Parse the raw payload for robust extraction of the username claim.
        var payloadJson = Base64UrlDecode(new JsonWebToken(idToken).EncodedPayload);
        using var payload = JsonDocument.Parse(payloadJson);

        return ExtractString(payload.RootElement, Config.UsernameClaim)
            ?? throw new InvalidOperationException($"id_token missing username claim '{Config.UsernameClaim}'.");
    }

    // Calls the OIDC UserInfo endpoint with the access token and extracts the configured role claim.
    // Fails closed (empty roles) on any error so the login gate in OidcController can deny access.
    private async Task<IReadOnlyList<string>> FetchUserInfoRolesAsync(string? accessToken, OpenIdConnectConfiguration config, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(config.UserInfoEndpoint))
        {
            return Array.Empty<string>();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, config.UserInfoEndpoint);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        var httpClient = _httpClientFactory.CreateClient(nameof(OidcService));
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return Array.Empty<string>();
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // UserInfo may be plain JSON or a signed JWT (application/jwt). Handle both.
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "application/jwt", StringComparison.OrdinalIgnoreCase) || IsCompactJwt(body))
        {
            body = Base64UrlDecode(new JsonWebToken(body.Trim()).EncodedPayload);
        }

        using var doc = JsonDocument.Parse(body);
        return ExtractStringArray(doc.RootElement, Config.RoleClaim);
    }

    private static bool IsCompactJwt(string value)
    {
        // A compact JWS has exactly two dots and no JSON object braces.
        var trimmed = value.Trim();
        return !trimmed.StartsWith('{') && trimmed.Count(c => c == '.') == 2;
    }

    private async Task<OpenIdConnectConfiguration> GetOpenIdConfigurationAsync(CancellationToken cancellationToken)
    {
        var issuer = (Config.OidcIssuer ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrEmpty(issuer))
        {
            throw new InvalidOperationException("OIDC issuer is not configured.");
        }

        ConfigurationManager<OpenIdConnectConfiguration> manager;
        lock (_configLock)
        {
            if (_configManager is null || !string.Equals(_configuredIssuer, issuer, StringComparison.Ordinal))
            {
                var metadataAddress = $"{issuer}/.well-known/openid-configuration";
                _configManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                    metadataAddress,
                    new OpenIdConnectConfigurationRetriever(),
                    new HttpDocumentRetriever(_httpClientFactory.CreateClient(nameof(OidcService))) { RequireHttps = issuer.StartsWith("https", StringComparison.OrdinalIgnoreCase) });
                _configuredIssuer = issuer;
            }

            manager = _configManager;
        }

        return await manager.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resets cached discovery metadata so the next request re-fetches (call on configuration change).
    /// </summary>
    public void InvalidateConfiguration()
    {
        lock (_configLock)
        {
            _configManager = null;
            _configuredIssuer = null;
        }
    }

    private static string? ExtractString(JsonElement root, string path)
    {
        return TryResolve(root, path, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }

    private static IReadOnlyList<string> ExtractStringArray(JsonElement root, string path)
    {
        if (!TryResolve(root, path, out var element))
        {
            return Array.Empty<string>();
        }

        return element.ValueKind switch
        {
            JsonValueKind.Array => element.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToArray(),
            JsonValueKind.String => new[] { element.GetString()! },
            _ => Array.Empty<string>(),
        };
    }

    // Resolves a dotted claim path (e.g. "realm_access.roles") against the payload JSON.
    private static bool TryResolve(JsonElement root, string path, out JsonElement element)
    {
        element = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
            {
                return false;
            }
        }

        return true;
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded,
        };
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }
}
