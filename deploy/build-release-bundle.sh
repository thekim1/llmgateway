#!/bin/sh
# Builds the deployment bundle attached to a GitHub release: the files a Docker host needs to run
# compose.prod.yaml (or compose.portainer.yaml), with the image defaults pinned to the released images.
#
#   GATEWAY_IMAGE=... ADMINAPI_IMAGE=... MIGRATIONS_IMAGE=... DATAAPI_IMAGE=... \
#     deploy/build-release-bundle.sh VERSION OUTPUT_DIR
#
# Each *_IMAGE is a full reference, ideally pinned by digest (registry/repo:version@sha256:...).
# Writes OUTPUT_DIR/ume-llm-gateway-VERSION.tar.gz (one top-level directory containing deploy/, so
# the README commands work unchanged) and OUTPUT_DIR/compose.portainer.yaml (paste into Portainer).
set -eu
version="${1:?usage: build-release-bundle.sh VERSION OUTPUT_DIR}"
out="${2:?usage: build-release-bundle.sh VERSION OUTPUT_DIR}"
: "${GATEWAY_IMAGE:?}" "${ADMINAPI_IMAGE:?}" "${MIGRATIONS_IMAGE:?}" "${DATAAPI_IMAGE:?}"
here="$(cd "$(dirname "$0")" && pwd)"
name="ume-llm-gateway-$version"
mkdir -p "$out"
out="$(cd "$out" && pwd)"
stage="$(mktemp -d)"
trap 'rm -r "$stage"' EXIT
mkdir -p "$stage/$name/deploy"

cd "$here"
cp -R compose.prod.yaml compose.portainer.yaml init-deployment.sh Backup-Database.ps1 Restore-Database.ps1 \
  postgres redis proxy otel "$stage/$name/deploy/"
cd "$stage/$name/deploy"
# Line endings matter: these files are mounted into Linux containers or run by sh.
find . -type f -exec sed -i 's/\r$//' {} +
chmod +x init-deployment.sh

# ${GATEWAY_IMAGE:-local default} and ${GATEWAY_IMAGE:?required} both become ${GATEWAY_IMAGE:-<released image>},
# so the variables still override the release.
for var in GATEWAY_IMAGE ADMINAPI_IMAGE MIGRATIONS_IMAGE DATAAPI_IMAGE; do
  ref="$(eval echo "\$$var")"
  sed -i -E 's#\$\{'"$var"':[-?][^}]*\}#${'"$var"':-'"$ref"'}#' compose.prod.yaml compose.portainer.yaml
done
for file in compose.prod.yaml compose.portainer.yaml; do
  grep -q 'image: \${[A-Z]*_IMAGE:-ume-llm-gateway/' "$file" && { echo "error: unpinned image left in $file" >&2; exit 1; }
done

cd "$stage"
tar -czf "$out/$name.tar.gz" "$name"
cp "$name/deploy/compose.portainer.yaml" "$out/compose.portainer.yaml"
echo "Wrote $out/$name.tar.gz and $out/compose.portainer.yaml"
