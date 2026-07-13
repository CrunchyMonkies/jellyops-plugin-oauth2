#!/usr/bin/env bash
#
# Build and push the Keycloak SSO plugin image-volume payload for JellyOps.
#
# Unlike jellyops-plugin-shoko (which packages a prebuilt release zip), this
# plugin's source lives in this repo under src/Plugin, so the payload is compiled
# here with `dotnet publish` and then stripped of host assemblies (the .csproj
# StripHostAssemblies target keeps only our DLL + the third-party OIDC/JWT stack).
#
# The plugin references the Jellyfin v12 assemblies. By default it builds against
# a sibling checkout of jellyfin-src (../../jellyfin-src relative to repo root, as
# in the JellyOps workspace). Override JELLYFIN_SRC if it lives elsewhere.
#
# The image is built with --provenance=false so it is a single plain manifest the
# kubelet mounts cleanly as an image volume.
#
# Usage:
#   docker/build-push.sh [TAG]
#
# Environment overrides:
#   REGISTRY     image repo prefix (default: ghcr.io/crunchymonkies/jellyops-plugin-oauth2)
#   IMAGE        full image repo   (default: <REGISTRY>/plugin)
#   JELLYFIN_SRC path to jellyfin-src checkout (default: <repo>/../jellyfin-src)
#   PUSH         push to registry  (default: true; set PUSH=false to build only)
set -euo pipefail

REGISTRY="${REGISTRY:-ghcr.io/crunchymonkies/jellyops-plugin-oauth2}"
IMAGE="${IMAGE:-${REGISTRY}/plugin}"
PUSH="${PUSH:-true}"
TAG="${1:-$(date +%Y%m).$(date +%d).1}"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
JELLYFIN_SRC="${JELLYFIN_SRC:-$(cd "$ROOT/.." && pwd)/jellyfin-src}"
PROJECT="$ROOT/src/Plugin/Jellyfin.Plugin.OAuth2.csproj"

[ -d "$JELLYFIN_SRC" ] || { echo "!! jellyfin-src not found at $JELLYFIN_SRC (set JELLYFIN_SRC)" >&2; exit 1; }

CTX="$(mktemp -d)"
trap 'rm -rf "$CTX"' EXIT
mkdir -p "$CTX/plugin"

echo ">> Publishing plugin (Release, net10.0) against $JELLYFIN_SRC"
dotnet publish "$PROJECT" -c Release -o "$CTX/publish" -v minimal

# The StripHostAssemblies target already removed Emby.*/Jellyfin.*/MediaBrowser.* from
# the build output; copy the published payload (our DLL + IdentityModel/JWT DLLs + meta.json).
cp "$CTX/publish/"*.dll "$CTX/plugin/"
cp "$CTX/publish/meta.json" "$CTX/plugin/meta.json"

# Bake the jellyops standard first-run hook. The operator auto-runs firstrun.sh (once)
# from the staged plugin dir to seed the OIDC configuration XML from env/Secrets.
cp "$ROOT/scripts/firstrun.sh" "$CTX/plugin/firstrun.sh"
chmod +x "$CTX/plugin/firstrun.sh"

# Sanity-check the payload before baking it into an image.
test -f "$CTX/plugin/Jellyfin.Plugin.OAuth2.dll" || { echo "!! plugin DLL missing from payload" >&2; exit 1; }
test -f "$CTX/plugin/meta.json"                  || { echo "!! meta.json missing from payload"  >&2; exit 1; }
test -f "$CTX/plugin/firstrun.sh"                || { echo "!! firstrun.sh missing from payload" >&2; exit 1; }
# Host assemblies must NOT ship (type-identity conflicts in the plugin ALC).
if ls "$CTX/plugin/"MediaBrowser.* "$CTX/plugin/"Jellyfin.[!P]* "$CTX/plugin/"Emby.* >/dev/null 2>&1; then
  echo "!! host assemblies leaked into payload — StripHostAssemblies failed" >&2; exit 1
fi
echo ">> Payload contents:"
( cd "$CTX/plugin" && ls -1 )

PLUGIN_VERSION="$(grep -oE '"version"[[:space:]]*:[[:space:]]*"[^"]+"' "$CTX/plugin/meta.json" | head -1 | grep -oE '[0-9.]+' || true)"
PLUGIN_VERSION="${PLUGIN_VERSION:-unknown}"
echo ">> Plugin version from meta.json: ${PLUGIN_VERSION}"

cp "$ROOT/docker/Dockerfile.plugin" "$CTX/Dockerfile.plugin"

echo ">> Building ${IMAGE} (tags: ${PLUGIN_VERSION}, ${TAG}, latest)"
docker build --provenance=false \
  -f "$CTX/Dockerfile.plugin" \
  -t "${IMAGE}:${PLUGIN_VERSION}" \
  -t "${IMAGE}:${TAG}" \
  -t "${IMAGE}:latest" \
  "$CTX"

if [ "$PUSH" = "true" ]; then
  echo ">> Pushing tags to ${IMAGE}"
  docker push "${IMAGE}:${PLUGIN_VERSION}"
  docker push "${IMAGE}:${TAG}"
  docker push "${IMAGE}:latest"
  DIGEST="$(docker inspect --format='{{range .RepoDigests}}{{println .}}{{end}}' "${IMAGE}:${PLUGIN_VERSION}" | grep "^${IMAGE}@" | head -1)"
  echo ""
  echo ">> Pushed. Pin this digest in k8s/30-jellyfinplugin.yaml (spec.pluginImage.reference):"
  echo "     ${DIGEST}"
else
  echo ">> PUSH=false — built locally only, not pushed."
fi
