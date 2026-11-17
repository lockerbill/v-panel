#!/usr/bin/env bash
# Installs (or upgrades) v-panel as a systemd service.
# Usage: sudo ./install.sh [path-to-published-folder]   (default: ./publish next to this script's parent)
set -euo pipefail

SRC="${1:-$(cd "$(dirname "$0")/.." && pwd)/publish}"
APP_DIR=/opt/vpanel/app
APPS_ROOT=/opt/vpanel/apps
CADDY_DIR=/opt/vpanel/caddy
DATA_DIR=/var/lib/vpanel
CONF_DIR=/etc/vpanel
SERVICE_USER=vpanel

if [[ $EUID -ne 0 ]]; then echo "Run as root (sudo)." >&2; exit 1; fi
if [[ ! -x "$SRC/VPanel.Web" ]]; then echo "Published binary not found in $SRC (run scripts/publish.sh first)." >&2; exit 1; fi
if ! command -v docker >/dev/null; then echo "Docker is required: https://docs.docker.com/engine/install/" >&2; exit 1; fi
if ! docker compose version >/dev/null 2>&1; then echo "The docker compose plugin is required." >&2; exit 1; fi

echo "==> Creating user and folders"
id -u "$SERVICE_USER" >/dev/null 2>&1 || useradd --system --home-dir "$DATA_DIR" --shell /usr/sbin/nologin "$SERVICE_USER"
usermod -aG docker "$SERVICE_USER"
install -d -o "$SERVICE_USER" -g "$SERVICE_USER" -m 750 "$DATA_DIR"
install -d -o "$SERVICE_USER" -g "$SERVICE_USER" -m 755 "$APPS_ROOT" "$CADDY_DIR" "$CADDY_DIR/sites"
install -d -m 755 "$APP_DIR" "$CONF_DIR"

echo "==> Copying binaries to $APP_DIR"
systemctl stop vpanel 2>/dev/null || true
rm -rf "${APP_DIR:?}"/*
cp -a "$SRC"/. "$APP_DIR"/
chmod +x "$APP_DIR/VPanel.Web"

if [[ ! -f "$CONF_DIR/appsettings.json" ]]; then
  cp "$(dirname "$0")/appsettings.json" "$CONF_DIR/appsettings.json"
fi

if [[ ! -f "$CONF_DIR/vpanel.env" ]]; then
  # Listen on localhost, plus the docker bridge so the shared Caddy container can proxy to v-panel.
  BRIDGE_IP=$(ip -4 addr show docker0 2>/dev/null | awk '/inet /{split($2,a,"/"); print a[1]; exit}')
  URLS="http://127.0.0.1:5080"
  [[ -n "${BRIDGE_IP:-}" ]] && URLS="$URLS;http://$BRIDGE_IP:5080"
  echo "ASPNETCORE_URLS=$URLS" > "$CONF_DIR/vpanel.env"
fi

echo "==> Installing systemd service"
cp "$(dirname "$0")/vpanel.service" /etc/systemd/system/vpanel.service
systemctl daemon-reload
systemctl enable --now vpanel

echo
echo "v-panel is running ($(cat "$CONF_DIR/vpanel.env"))."
echo "Open it through an SSH tunnel:   ssh -L 5080:127.0.0.1:5080 <user>@<server>   then http://localhost:5080"
echo "First visit creates the admin account. Then deploy 'shared-infra' (Caddy + Postgres) and set"
echo "Settings > v-panel public URL to expose the panel over HTTPS."
echo "Logs: journalctl -u vpanel -f"
