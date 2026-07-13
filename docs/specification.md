# jellyops-plugin-oauth2 — Keycloak SSO for Jellyfin v12

Draft `v0.1`

## 1. Goal

Deliver Keycloak/OIDC single sign-on with **auto-login** to a JellyOps-managed Jellyfin v12
instance. A visitor hitting the Jellyfin URL is (optionally) redirected to Keycloak, authenticates
once, and lands in the Jellyfin web client already signed in. Native/mobile apps continue to work
because the plugin mints real Jellyfin tokens.

### Non-goals

- Not a reverse-proxy header-auth (ext-auth) design. The gateway only *routes* and optionally
  *redirects*; it does not authenticate requests. (Core Jellyfin has no trusted-header auth.)
- Not a Keycloak deployment. Keycloak is external and referenced by issuer URL.
- Not a fork of jellyfin-web. Token hand-off uses the web client's existing localStorage credential
  store, so no web-client source change is required.

## 2. Architecture

Three cooperating layers:

1. **Plugin (this repo, `src/Plugin`)** — the OIDC brains. Runs entirely inside Jellyfin.
2. **Packaging (this repo)** — OCI image-volume payload + `JellyfinPlugin` CR + first-run config seed.
3. **Operator (`jellyops`)** — a toggleable gateway redirect that starts the flow (`spec.gateway.sso`).

### 2.1 Plugin components

| Type | Role |
| --- | --- |
| `Plugin` | `BasePlugin<PluginConfiguration>, IHasWebPages`; GUID `f1e2d3c4-b5a6-4978-8a9b-0c1d2e3f4a5b`; serves the dashboard settings page. |
| `PluginConfiguration` | Issuer, client id/secret, scopes, username/role claims, admin roles, provisioning + role-sync toggles, public origin. |
| `PluginServiceRegistrator` | Registers `OidcService`, `OidcStateStore`, the named `HttpClient`, and `IAuthenticationProvider`. |
| `OidcService` | OIDC discovery (`ConfigurationManager<OpenIdConnectConfiguration>`), PKCE authorize URL, code exchange, id_token validation (`JsonWebTokenHandler`), claim extraction (dot-path aware). |
| `OidcStateStore` | Single-use, time-boxed `state → PKCE verifier` map (CSRF + PKCE). |
| `OidcController` | `[AllowAnonymous]` `/sso/authorize` + `/sso/callback`; auto-discovered by the host. |
| `OidcAuthenticationProvider` | `IAuthenticationProvider` owning provisioned users so local password login is refused (SSO-only). |

### 2.2 Login sequence

1. `GET /sso/authorize` — if already authenticated, 302 `/web/`. Else generate `state` + PKCE,
   store them, and 302 to Keycloak's authorization endpoint.
2. Keycloak authenticates the user and redirects to `GET /sso/callback?code&state`.
3. Callback consumes `state`, exchanges `code` (+ PKCE verifier + client secret) for tokens, and
   validates the id_token against the discovered issuer/JWKS with audience = client id. The
   username comes from the validated id_token; **roles are then fetched from the OIDC UserInfo
   endpoint** (`GET userinfo` with the access token) and extracted via the dot-path `RoleClaim`
   (default `realm_access.roles`). UserInfo failure yields empty roles (fail-closed at the gate).
4. **Access gate**: if `AllowedRoles` is non-empty, the user must hold an allowed role (an admin
   role also counts) or sign-in is refused with 403 and no account is created. Then the username
   resolves the Jellyfin user; if missing and provisioning is enabled, it is created
   (`IUserManager.CreateUserAsync`), stamped to this provider, and given an initial policy (admin
   from `AdminRoles`). Existing users optionally get their admin flag re-synced (`SyncRolesOnLogin`).
5. `ISessionManager.AuthenticateDirect` mints a native token (no password check) — same mechanism
   as QuickConnect (`QuickConnectManager.AuthorizeRequest`).
6. The callback returns a tiny HTML page that upserts a server entry into the web client's
   `jellyfin_credentials` localStorage (matched by server `Id` = `SystemId`) with the token +
   user id + origin, then navigates to `/web/`, where the SPA auto-connects signed-in.

### 2.3 Token compatibility

The minted token is a `Device.AccessToken` row; it validates through the normal
`CustomAuthenticationHandler` → `AuthorizationContext` path (device lookup by token → user). So any
client — web or native — authenticates with `Authorization: MediaBrowser Token="…"`.

## 3. Configuration seeding

The operator auto-runs `firstrun.sh` (baked in the image) once per instance. It writes
`/config/plugins/configurations/Jellyfin.Plugin.OAuth2.xml` (root `<PluginConfiguration>`, the class
name the host `XmlSerializer` binds) from `spec.install.env` (backed by the `oidc-creds` Secret).
`failurePolicy: Ignore` lets Jellyfin boot even if seeding fails (admin can finish in the dashboard).

## 4. Packaging

- `FROM scratch`; root = plugin dir (`Jellyfin.Plugin.OAuth2.dll`, `meta.json`, `firstrun.sh`, and
  the third-party `Microsoft.IdentityModel.*` / `System.IdentityModel.Tokens.Jwt` DLLs).
- The `.csproj` `StripHostAssemblies` target removes Emby/Jellyfin/MediaBrowser DLLs so only the
  plugin + genuinely-third-party deps ship (no type-identity conflicts in the plugin ALC).
- Built with `--provenance=false` → a single plain manifest the kubelet mounts as an image volume.
- `injection: imageVolumeCopy` stages the read-only payload into a writable plugins dir.

## 5. Gateway auto-login (operator side)

`jellyops` `GatewaySpec` gains an `sso` block:

```yaml
spec:
  gateway:
    sso:
      autoLoginRedirect: true          # on/off toggle
      authorizePath: /sso/authorize    # default
```

When enabled, `BuildHTTPRoute`'s exact-`/` rule redirects to `authorizePath` instead of `/web/`.
When disabled/unset, the current `/` → `/web/` behavior is preserved byte-for-byte. `/sso/*` already
reaches the server Service via the existing prefix-`/` catch-all, so no new route is needed.

## 6. Security notes

- Authorization Code **+ PKCE (S256)**; confidential client secret sent only server-to-server on the
  token endpoint.
- id_token signature/issuer/audience/lifetime validated against live JWKS.
- `state` is single-use and time-boxed (CSRF).
- Client secret is stored in the plugin config XML (`/config`) — keep `/config` access restricted;
  supply the secret via a managed/sealed Secret, never commit it.
