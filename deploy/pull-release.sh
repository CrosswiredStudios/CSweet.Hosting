#!/usr/bin/env bash
# Outbound updater for the public Hosting release channel on the Mac Studio.
set -euo pipefail
umask 077
root=${HOSTING_DEPLOY_DIR:-"$HOME/csweet-hosting"}
export HOSTING_DEPLOY_DIR="$root"
export PATH="/usr/local/bin:/opt/homebrew/bin:$HOME/.docker/bin:$PATH"
[[ ! -f "$root/pause-updates" ]] || exit 0
repo=CrosswiredStudios/CSweet.Hosting
metadata=$(curl --proto '=https' --fail --silent --show-error --max-time 30 \
  "https://api.github.com/repos/$repo/releases/latest")
version=$(jq -er 'select(.draft == false and .prerelease == false) | .tag_name' <<< "$metadata")
[[ "$version" =~ ^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]
if [[ -f "$root/current-version" && "$(cat "$root/current-version")" == "$version" ]]; then exit 0; fi
stage=$(mktemp -d)
files=(hosting-image.tar.gz compose.hosting.yml deploy.sh version revision SHA256SUMS)
cleanup() {
  for file in "${files[@]}"; do rm -f "$stage/$file"; done
  rmdir "$stage"
}
trap cleanup EXIT
for file in "${files[@]}"; do
  digest=$(jq -er --arg file "$file" '[.assets[] | select(.name == $file and .state == "uploaded")] | select(length == 1) | .[0].digest' <<< "$metadata")
  [[ "$digest" =~ ^sha256:[0-9a-f]{64}$ ]]
  curl --proto '=https' --proto-redir '=https' -fsSL --retry 2 --max-time 600 -o "$stage/$file" \
    "https://github.com/$repo/releases/download/$version/$file"
  actual=$(shasum -a 256 "$stage/$file")
  [[ "${actual%% *}" == "${digest#sha256:}" ]] || { echo "Bad release asset digest: $file" >&2; exit 1; }
done
[[ "$(cat "$stage/version")" == "$version" ]]
bash "$stage/deploy.sh"
