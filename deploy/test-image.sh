#!/usr/bin/env bash
set -euo pipefail
image=${1:?supply the Hosting image}
scratch=$(mktemp -d)
container=
cleanup() {
  if [[ -n "$container" ]]; then docker rm -fv "$container" >/dev/null; fi
  rm -f "$scratch/service_key" "$scratch/nas_key" "$scratch/cf_token"
  rmdir "$scratch"
}
trap cleanup EXIT
# Fixture values, readable by the non-root container. No network egress.
for key in service_key nas_key cf_token; do printf 'test-only' > "$scratch/$key"; done
chmod 755 "$scratch"
chmod 644 "$scratch"/*
container=$(docker run -d --network none --read-only --cap-drop ALL --security-opt no-new-privileges \
  --mount "type=bind,source=$scratch,target=/run/secrets,readonly" --tmpfs /tmp:mode=1777 \
  --tmpfs /state:uid=10001,mode=0700 -e POOL_ID=fixture -e TEMPLATE_NAME=fixture \
  -e HQ_IMAGE="fixture@sha256:$(printf '%064d' 0)" -e TRUENAS_URL=wss://fixture.invalid/api/current \
  -e NAS_MEDIA_PATH=/mnt/test -e CF_ACCOUNT_ID=test -e CF_ZONE_ID=test "$image")
for ((i=0; i<30; i++)); do
  if docker exec "$container" curl -fsS http://127.0.0.1:8080/health >/dev/null 2>&1; then break; fi
  sleep 1
done
docker exec "$container" curl -fsS http://127.0.0.1:8080/health
url=http://127.0.0.1:8080/v1/deployments/00000000-0000-0000-0000-000000000001
status=$(docker exec "$container" curl -s -o /dev/null -w '%{http_code}' -X PUT "$url")
[[ "$status" == 401 ]]
status=$(docker exec "$container" curl -s -o /dev/null -w '%{http_code}' -X PUT -d '{}' -H 'Authorization: Bearer test-only' "$url")
[[ "$status" == 400 ]]
