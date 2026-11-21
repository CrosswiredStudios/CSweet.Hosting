#!/bin/bash
set -euo pipefail
umask 077
export DEBIAN_FRONTEND=noninteractive
if ! docker compose version >/dev/null 2>&1; then
  apt-get -o Acquire::Retries=3 update
  apt-get -y install docker.io docker-compose-v2 openssl curl
fi
systemctl enable --now docker
install -d -m 0700 /var/lib/csweet-bootstrap
cd /var/lib/csweet-bootstrap
python3 /opt/csweet-hosting/bootstrap.py
docker compose -f compose.json up -d --wait postgres
docker compose -f compose.json run --rm migrate
docker compose -f compose.json up -d api agent worker gateway git provisioner
# This image must enforce the reserved owner before we expose first-run setup.
python3 /opt/csweet-hosting/bootstrap.py verify
docker compose -f compose.json up -d connector
