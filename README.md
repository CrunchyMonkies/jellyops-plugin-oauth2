# jellyops-plugin-oauth2

A [JellyOps](../jellyops) plugin package that delivers **Keycloak / OpenID Connect single sign-on
with auto-login** to a JellyOps-managed **Jellyfin v12 (.NET 10)** instance as a `JellyfinPlugin`
custom resource.

Unlike the community `9p4/jellyfin-plugin-sso` (which targets Jellyfin 10.x), this plugin is built
fresh for the Jellyfin **v12 ABI (`12.0.0.0`, `net10.0`)** used across the JellyOps workspace, and
is packaged as an OCI **image-volume** payload rather than a classic plugin zip.

## How it works

```
Browser → GET /sso/authorize        (plugin, [AllowAnonymous])
        → 302 to Keycloak           (Authorization Code + PKCE)
   user authenticates in Keycloak
        → GET /sso/callback?code…    (plugin)
             ├─ exchange code → tokens, validate id_token (issuer/audience/JWKS)
             ├─ provision/lookup Jellyfin user (IUserManager), map admin from roles
             ├─ mint a NATIVE Jellyfin token (ISessionManager.AuthenticateDirect — no password)
             └─ return a page that seeds the web client's localStorage credentials
        → /web/ … already signed in
```

Because the plugin mints a **real Jellyfin access token** (the same kind QuickConnect issues), native
apps keep working — SSO is not a reverse-proxy header trick.

**Auto-login** is triggered at the gateway: the JellyOps operator can redirect the site entry path to
`/sso/authorize` when `spec.gateway.sso.autoLoginRedirect` is enabled on the `Jellyfin` CR
(toggleable on/off). `/sso/authorize` short-circuits already-authenticated callers back to `/web/`,
so there is no redirect loop.

## Repository layout

| Path | Purpose |
| --- | --- |
| `src/Plugin/` | The C# plugin (v12/net10). `Plugin.cs`, `PluginServiceRegistrator.cs`, `Api/OidcController.cs`, `Auth/OidcAuthenticationProvider.cs`, `Services/OidcService.cs`, `Configuration/`. |
| `docker/Dockerfile.plugin` | `FROM scratch` image-volume payload (plugin dir at root). |
| `docker/build-push.sh` | Compiles `src/Plugin`, assembles `plugin/`, builds & pushes the image. |
| `scripts/firstrun.sh` | Operator-run first-run hook; seeds `Jellyfin.Plugin.OAuth2.xml` from env/Secret. |
| `.github/workflows/plugin-image.yml` | CI: publish plugin → scratch image → GHCR (`provenance:false`). |
| `k8s/30-jellyfinplugin.yaml` | The `JellyfinPlugin` CR (external Keycloak; no companion workload). |
| `k8s/35-oidc-credentials.yaml` | Secret template (issuer / client-id / client-secret). |
| `keycloak/realm-media.json` | Importable example realm (roles, confidential client, UserInfo roles mapper, sample users). |

## Roles: access restriction & admin mapping

Roles are read from the OIDC **UserInfo** endpoint (default claim `realm_access.roles`) after the
id_token is validated. Two independent controls:

- **`OIDC_ALLOWED_ROLES`** (login gate) — if set, a user must hold one of these roles (e.g.
  `jellyfin`) to sign in at all. Users without it get **403 and are never provisioned**. Leave it
  empty to allow any authenticated Keycloak user.
- **`OIDC_ADMIN_ROLES`** — users holding one of these (e.g. `jellyfin-admin`) become Jellyfin
  administrators; holding an admin role also satisfies the login gate.

`SyncRolesOnLogin` re-applies the admin flag on every login so promoting/demoting in Keycloak takes
effect at next sign-in. (Per-library / per-permission mapping is not implemented — provisioned users
get access to all libraries.)

## Keycloak setup

Import the ready-made realm, or configure an equivalent client yourself.

```bash
# Import the example realm (roles jellyfin/jellyfin-admin, a confidential 'jellyfin' client with a
# roles→UserInfo mapper, and sample users alice/bob). Edit the client secret + redirect URI first.
/opt/keycloak/bin/kcadm.sh create realms -f keycloak/realm-media.json
# or run Keycloak with:  --import-realm  (with the file mounted under /opt/keycloak/data/import/)
```

If configuring by hand, the client must be **confidential** with:

- **Standard flow** (Authorization Code) enabled, PKCE `S256`
- a **client secret**
- valid redirect URI `https://<your-jellyfin-host>/sso/callback`
- the **realm-roles protocol mapper** with **Add to userinfo** enabled (claim `realm_access.roles`),
  so the plugin can read roles from UserInfo; assign users the `jellyfin` / `jellyfin-admin` roles.

## Build & deploy

```bash
# Build against the sibling jellyfin-src checkout, push, and print the digest to pin.
REGISTRY=ghcr.io/you/jellyops-plugin-oauth2 docker/build-push.sh

# Pin the printed @sha256 digest into k8s/30-jellyfinplugin.yaml (spec.pluginImage.reference),
# fill in k8s/35-oidc-credentials.yaml, then:
kubectl apply -f k8s/35-oidc-credentials.yaml
kubectl apply -f k8s/30-jellyfinplugin.yaml
```

Enabling gateway auto-login is done on the `Jellyfin` CR (operator side):

```yaml
spec:
  gateway:
    sso:
      autoLoginRedirect: true        # toggle SSO auto-login on/off
      # authorizePath: /sso/authorize  # default
```

See `docs/specification.md` for the full design.
