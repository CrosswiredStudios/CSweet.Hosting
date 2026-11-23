#!/usr/bin/env bash
# Emit only ISO bytes to stdout. A registry credential stays on the provisioner.
set -euo pipefail
umask 077
args=(-quiet -volid cidata -joliet -rock -iso-level 3 -graft-points meta-data user-data)
if [[ $# == 2 ]]; then
  release=$1
  tag=$2
  digest=${release##*@sha256:}
  [[ "$digest" =~ ^[0-9a-f]{64}$ && "$release" == "ghcr.io/crosswiredstudios/csweet-headquarters-hosted@sha256:$digest" && "$tag" == "csweet-headquarters:sha256-$digest" ]]
  root=${HQ_IMAGE_CACHE:-/state/headquarters-images}
  mkdir -p "$root"
  dir="$root/$digest"
  (
    flock -x 9
    if [[ ! -f "$dir/headquarters.sha256" ]] || ! (cd "$dir" && sha256sum --status -c headquarters.sha256); then
      stage="$root/.$digest.partial"
      rm -rf -- "$stage"
      mkdir "$stage"
      trap 'rm -rf -- "$stage"' EXIT
      auth=/run/hosting-registry/auth.json
      opts=()
      if [[ -f "$auth" ]]; then opts+=(--src-authfile "$auth"); fi
      skopeo --tmpdir "$stage" --override-os linux --override-arch amd64 copy "${opts[@]}" \
        "docker://$release" "docker-archive:$stage/headquarters.tar:$tag" >&2
      (cd "$stage" && sha256sum headquarters.tar > headquarters.sha256)
      if [[ -d "$dir" ]]; then rm -rf -- "$dir"; fi
      mv -T "$stage" "$dir"
    fi
  ) 9>"$root/$digest.lock"
  args+=("headquarters.tar=$dir/headquarters.tar" "headquarters.sha256=$dir/headquarters.sha256")
elif [[ $# != 0 ]]; then
  exit 2
fi
exec genisoimage "${args[@]}"
