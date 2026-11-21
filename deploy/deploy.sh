#!/usr/bin/env bash
# Run on the Mac from a downloaded release, or pass --rollback vX.Y.Z.
set -euo pipefail
umask 077
root=${HOSTING_DEPLOY_DIR:-"$HOME/csweet-hosting"}
bundle=$(cd "$(dirname "$0")" && pwd)
[[ "$root" = /* && -f "$root/hosting.env" ]] || { echo "Configure hosting.env under an absolute HOSTING_DEPLOY_DIR first." >&2; exit 1; }
for file in hosting-service-key.txt hosting-nas-ca.pem hosting-nas-key.txt hosting-cloudflare-token.txt; do
  [[ -s "$root/secrets/$file" ]] || { echo "Missing secrets/$file" >&2; exit 1; }
done
mkdir -p "$root/releases"
mkdir "$root/.deploy-lock" || { echo 'Another deployment owns .deploy-lock.' >&2; exit 1; }
trap 'rmdir "$root/.deploy-lock"' EXIT
version_pattern='^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'
if [[ ${1:-} == --rollback && $# == 2 ]]; then
  version=$2
  [[ $version =~ $version_pattern ]] && [[ -s "$root/releases/$version/image-id" ]] || exit 2
elif [[ $# == 0 ]]; then
  (cd "$bundle" && shasum -a 256 -c SHA256SUMS)
  version=$(cat "$bundle/version")
  revision=$(cat "$bundle/revision")
  [[ $version =~ $version_pattern && $revision =~ ^[0-9a-f]{40}$ ]] || exit 2
  docker load -i "$bundle/hosting-image.tar.gz"
  image="csweet-hosting:$version"
  [[ $(docker image inspect -f '{{.Os}}/{{.Architecture}}' "$image") == linux/arm64 ]]
  [[ $(docker image inspect -f '{{index .Config.Labels "org.opencontainers.image.revision"}}' "$image") == "$revision" ]]
  [[ $(docker image inspect -f '{{index .Config.Labels "org.opencontainers.image.version"}}' "$image") == "$version" ]]
  image_id=$(docker image inspect -f '{{.Id}}' "$image")
  dest="$root/releases/$version"
  mkdir -p "$dest"
  if [[ -e "$dest/image-id" ]] && [[ $(cat "$dest/image-id") != "$image_id" ]]; then
    echo 'This version is already installed with a different image.' >&2
    exit 1
  fi
  cp "$bundle/compose.hosting.yml" "$dest/compose.hosting.yml"
  printf '%s\n' "$image_id" > "$dest/image-id"
else
  echo 'Usage: bash deploy.sh [--rollback vX.Y.Z]' >&2
  exit 2
fi

compose() {
  local release=$1
  shift
  # An image ID also makes rollback independent of mutable local Docker tags.
  HOSTING_RELEASE_VERSION="$release" HOSTING_IMAGE="$(cat "$root/releases/$release/image-id")" docker compose \
    --project-directory "$root" --env-file "$root/hosting.env" -p csweet-hosting \
    -f "$root/releases/$release/compose.hosting.yml" "$@"
}

activate() {
  local release=$1 id address status
  compose "$release" up -d --no-build --no-deps --wait --wait-timeout 90 provisioner || return 1
  id=$(compose "$release" ps -q provisioner) || return 1
  [[ -n "$id" && $(docker inspect -f '{{.Image}}' "$id") == "$(cat "$root/releases/$release/image-id")" ]] || return 1
  address=$(compose "$release" port provisioner 8080) || return 1
  [[ "$address" == 127.0.0.1:* ]] || return 1
  curl -fsS --noproxy '*' --max-time 5 "http://$address/health" >/dev/null || return 1
  # An authorized, deliberately incomplete request exercises the live API without allocating a VM.
  status=$( { printf 'Authorization: Bearer '; tr -d '\r\n' < "$root/secrets/hosting-service-key.txt"; printf '\n'; } |
    curl --noproxy '*' --max-time 5 -sS -o /dev/null -w '%{http_code}' -X PUT -H @- -H 'Content-Type: application/json' \
      --data '{}' "http://$address/v1/deployments/00000000-0000-0000-0000-000000000001") || return 1
  [[ "$status" == 400 ]]
}

previous=
if [[ -f "$root/current-version" ]]; then
  previous=$(cat "$root/current-version")
  [[ $previous =~ $version_pattern && -s "$root/releases/$previous/image-id" ]] || { echo 'Invalid installed release pointer.' >&2; exit 1; }
fi
if ! activate "$version"; then
  echo "Hosting $version failed its live check." >&2
  if [[ -n "$previous" && "$previous" != "$version" ]]; then
    if activate "$previous"; then echo "Restored $previous." >&2; else echo "Rollback to $previous also failed." >&2; fi
  fi
  exit 1
fi
printf '%s\n' "$version" > "$root/current-version.next"
mv "$root/current-version.next" "$root/current-version"
echo "Hosting $version is serving the authenticated API on $(hostname)."
