#!/bin/sh
#
# Keycloak SSO FIRST-RUN hook — runs ONCE per Jellyfin instance, the first time
# the plugin starts (tracked by the operator under /config/.jellyops/firstrun/).
# Baked into the plugin image root (plugin/firstrun.sh) and auto-detected by the
# JellyOps operator for imageVolumeCopy plugins; the CR only supplies env.
#
# Responsibility: seed the declarative plugin configuration XML that the plugin
# deserializes on load, so the Keycloak realm / client credentials come from the
# JellyfinPlugin CR (env + Secret) rather than being typed into the dashboard.
#
# The config filename is derived from the plugin assembly name:
#   /config/plugins/configurations/Jellyfin.Plugin.OAuth2.xml
# The root element MUST be <PluginConfiguration> (the config class name) so the
# host XmlSerializer binds it.
#
# Env (from spec.install.env / Secret):
#   OIDC_ISSUER              [required]  e.g. https://keycloak.example.com/realms/media
#   OIDC_CLIENT_ID           [required]
#   OIDC_CLIENT_SECRET       [required]
#   OIDC_SCOPES              [openid profile email]
#   OIDC_USERNAME_CLAIM      [preferred_username]
#   OIDC_ROLE_CLAIM          [realm_access.roles]   dotted path; roles read from UserInfo
#   OIDC_ALLOWED_ROLES       [""]        comma-separated; if set, user must hold one to sign in
#   OIDC_ADMIN_ROLES         [""]        comma-separated role values granting admin
#   OIDC_PUBLIC_ORIGIN       [""]        e.g. https://jellyfin.example.com
#   OIDC_ENABLE_PROVISIONING [true]
#   OIDC_SYNC_ROLES          [true]
#   OIDC_AUTO_LOGIN          [false]     informational; gateway owns the actual redirect
#
# failurePolicy: Ignore is recommended — a seed failure leaves Jellyfin to boot
# with an unconfigured plugin (admin can then fill in the dashboard settings).
set -eu

CONFIG_DIR="${CONFIG_DIR:-/config}"
CFG_DIR="${CONFIG_DIR}/plugins/configurations"
CFG="${CFG_DIR}/Jellyfin.Plugin.OAuth2.xml"

: "${OIDC_ISSUER:?OIDC_ISSUER is required}"
: "${OIDC_CLIENT_ID:?OIDC_CLIENT_ID is required}"
: "${OIDC_CLIENT_SECRET:?OIDC_CLIENT_SECRET is required}"

OIDC_SCOPES="${OIDC_SCOPES:-openid profile email}"
OIDC_USERNAME_CLAIM="${OIDC_USERNAME_CLAIM:-preferred_username}"
OIDC_ROLE_CLAIM="${OIDC_ROLE_CLAIM:-realm_access.roles}"
OIDC_ALLOWED_ROLES="${OIDC_ALLOWED_ROLES:-}"
OIDC_ADMIN_ROLES="${OIDC_ADMIN_ROLES:-}"
OIDC_PUBLIC_ORIGIN="${OIDC_PUBLIC_ORIGIN:-}"
OIDC_ENABLE_PROVISIONING="${OIDC_ENABLE_PROVISIONING:-true}"
OIDC_SYNC_ROLES="${OIDC_SYNC_ROLES:-true}"
OIDC_AUTO_LOGIN="${OIDC_AUTO_LOGIN:-false}"

mkdir -p "$CFG_DIR"

# Minimal XML escaper for element text (& < > " ').
xml_escape() {
  printf '%s' "$1" | sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g' -e 's/"/\&quot;/g' -e "s/'/\&apos;/g"
}

# Emit indented <string>...</string> entries for a comma-separated role list ($1).
roles_xml() {
  _out=""
  _IFS_SAVE="$IFS"
  IFS=','
  for _role in $1; do
    _role_trimmed="$(printf '%s' "$_role" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')"
    [ -n "$_role_trimmed" ] || continue
    _out="${_out}    <string>$(xml_escape "$_role_trimmed")</string>
"
  done
  IFS="$_IFS_SAVE"
  printf '%s' "$_out"
}

# Normalize truthy strings to XML boolean literals.
xml_bool() {
  case "$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]')" in
    1|true|yes|on) printf 'true' ;;
    *) printf 'false' ;;
  esac
}

echo ">> Seeding Keycloak SSO config -> $CFG"
cat > "$CFG" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<PluginConfiguration xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <OidcIssuer>$(xml_escape "$OIDC_ISSUER")</OidcIssuer>
  <ClientId>$(xml_escape "$OIDC_CLIENT_ID")</ClientId>
  <ClientSecret>$(xml_escape "$OIDC_CLIENT_SECRET")</ClientSecret>
  <Scopes>$(xml_escape "$OIDC_SCOPES")</Scopes>
  <UsernameClaim>$(xml_escape "$OIDC_USERNAME_CLAIM")</UsernameClaim>
  <RoleClaim>$(xml_escape "$OIDC_ROLE_CLAIM")</RoleClaim>
  <AllowedRoles>
$(roles_xml "$OIDC_ALLOWED_ROLES")  </AllowedRoles>
  <AdminRoles>
$(roles_xml "$OIDC_ADMIN_ROLES")  </AdminRoles>
  <EnableUserProvisioning>$(xml_bool "$OIDC_ENABLE_PROVISIONING")</EnableUserProvisioning>
  <SyncRolesOnLogin>$(xml_bool "$OIDC_SYNC_ROLES")</SyncRolesOnLogin>
  <PublicOrigin>$(xml_escape "$OIDC_PUBLIC_ORIGIN")</PublicOrigin>
  <AutoLoginEnabled>$(xml_bool "$OIDC_AUTO_LOGIN")</AutoLoginEnabled>
</PluginConfiguration>
EOF

echo ">> Keycloak SSO config seeded."
