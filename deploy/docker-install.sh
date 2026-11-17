#!/usr/bin/env bash
# Installs or upgrades v-panel as a Docker container.
# Usage (from the repository root):  sudo ./deploy/docker-install.sh [--pull]
#   default : build the image from this checkout
#   --pull  : use the published image ghcr.io/lockerbill/v-panel:latest instead of building
set -euo pipefail

cd "$(dirname "$0")/.."
COMPOSE_FILE=deploy/docker-compose.yml
ROOT=/opt/vpanel
NETWORK=vpanel-shared

if ! command -v docker >/dev/null; then echo "Docker is required: https://docs.docker.com/engine/install/" >&2; exit 1; fi
if ! docker compose version >/dev/null 2>&1; then echo "The docker compose plugin is required." >&2; exit 1; fi
if ! docker info >/dev/null 2>&1; then echo "Cannot talk to the Docker daemon (run with sudo?)." >&2; exit 1; fi

echo "==> Preparing $ROOT"
mkdir -p "$ROOT/apps" "$ROOT/caddy/sites" "$ROOT/data"
chmod 750 "$ROOT/data"

if ! docker network inspect "$NETWORK" >/dev/null 2>&1; then
  echo "==> Creating docker network $NETWORK"
  docker network create "$NETWORK" >/dev/null
fi

if [[ "${1:-}" == "--pull" ]]; then
  echo "==> Pulling image"
  docker compose -f "$COMPOSE_FILE" pull
  docker compose -f "$COMPOSE_FILE" up -d
else
  echo "==> Building image and starting v-panel"
  docker compose -f "$COMPOSE_FILE" up -d --build
fi

echo
echo "v-panel is running on 127.0.0.1:5080 (container 'vpanel')."
echo "Open it through an SSH tunnel:  ssh -L 5080:127.0.0.1:5080 <user>@<server>   then http://localhost:5080"
echo "First visit creates the admin account. Then deploy 'shared-infra' (Caddy + Postgres) and set"
echo "Settings > v-panel public URL to expose the panel over HTTPS."
echo "Logs: docker logs -f vpanel     Backup: everything lives in $ROOT"
