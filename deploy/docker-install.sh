#!/usr/bin/env bash
# Installs or upgrades v-panel as a Docker container.
# Usage (from the repository root):  sudo ./deploy/docker-install.sh [--pull] [--adopt [ROOT ...]] [--yes]
#   default        : build the image from this checkout
#   --pull         : use the published image ghcr.io/lockerbill/v-panel:latest instead of building
#   --adopt [ROOT] : server with stacks installed by hand before v-panel (shared Caddy/Postgres stack, app stacks).
#                    Joins the network of the running Caddy, mounts the stacks' folders into the v-panel container at the
#                    same paths and lists them as adopted roots, so they can be imported from the Import page in place.
#                    Without ROOTs, they are derived from the working folders of the existing compose projects.
#   --yes          : do not ask for confirmation
# The --adopt choices are saved in /opt/vpanel/vpanel.env and /opt/vpanel/docker-compose.adopt.yml and reused by later
# upgrades (re-run with --adopt to regenerate them).
set -euo pipefail

cd "$(dirname "$0")/.."
COMPOSE_FILE=deploy/docker-compose.yml
ROOT=/opt/vpanel
ENV_FILE=$ROOT/vpanel.env
ADOPT_FILE=$ROOT/docker-compose.adopt.yml
NETWORK=vpanel-shared

PULL=0 ADOPT=0 YES=0
ROOTS=()
for arg in "$@"; do
  case "$arg" in
    --pull) PULL=1 ;;
    --adopt) ADOPT=1 ;;
    --yes|-y) YES=1 ;;
    -*) echo "Unknown option $arg" >&2; exit 1 ;;
    *)
      if [[ $ADOPT -ne 1 ]]; then echo "Folders are only accepted after --adopt." >&2; exit 1; fi
      ROOTS+=("${arg%/}") ;;
  esac
done

if ! command -v docker >/dev/null; then echo "Docker is required: https://docs.docker.com/engine/install/" >&2; exit 1; fi
if ! docker compose version >/dev/null 2>&1; then echo "The docker compose plugin is required." >&2; exit 1; fi
if ! docker info >/dev/null 2>&1; then echo "Cannot talk to the Docker daemon (run with sudo?)." >&2; exit 1; fi

echo "==> Preparing $ROOT"
mkdir -p "$ROOT/apps" "$ROOT/caddy/sites" "$ROOT/data"
chmod 750 "$ROOT/data"

# Top folder of a stack: /opt/<x>, /srv/<x>, /root/<x>, /home/<user>/<x>; other paths are used as they are.
stack_root() {
  local p="${1%/}"
  IFS=/ read -r -a parts <<< "${p#/}"
  case "${parts[0]:-}" in
    home) [[ ${#parts[@]} -ge 3 ]] && { echo "/home/${parts[1]}/${parts[2]}"; return; } ;;
    opt|srv|root|data|docker|apps|stacks) [[ ${#parts[@]} -ge 2 ]] && { echo "/${parts[0]}/${parts[1]}"; return; } ;;
  esac
  echo "$p"
}

if [[ $ADOPT -eq 1 ]]; then
  echo "==> Looking for the existing shared infrastructure"
  CADDY=$(docker ps -a --format '{{.Names}}\t{{.Image}}' | awk -F'\t' '$2 ~ /(^|\/)caddy(:|@|$)/ {print $1; exit}')
  if [[ -n "$CADDY" ]]; then
    best=0
    for net in $(docker inspect -f '{{range $k, $v := .NetworkSettings.Networks}}{{$k}} {{end}}' "$CADDY"); do
      [[ "$net" == bridge || "$net" == host || "$net" == none ]] && continue
      count=$(docker network inspect -f '{{len .Containers}}' "$net" 2>/dev/null || echo 0)
      if [[ $count -gt $best ]]; then best=$count; NETWORK=$net; fi
    done
    echo "    Caddy container: $CADDY, shared network: $NETWORK"
  else
    echo "    No Caddy container found; keeping network $NETWORK"
  fi

  if [[ ${#ROOTS[@]} -eq 0 ]]; then
    while IFS= read -r dir; do
      [[ -z "$dir" || "$dir" == "$ROOT" || "$dir" == "$ROOT"/* ]] && continue
      ROOTS+=("$(stack_root "$dir")")
    done < <(docker ps -a --filter label=com.docker.compose.project \
               --format '{{.Label "com.docker.compose.project.working_dir"}}' | sort -u)
  fi
  # unique, and drop folders already covered by another root
  mapfile -t ROOTS < <(printf '%s\n' "${ROOTS[@]}" | sort -u)
  KEEP=()
  for r in "${ROOTS[@]}"; do
    covered=0
    for o in "${ROOTS[@]}"; do [[ "$o" != "$r" && "$r" == "$o"/* ]] && covered=1; done
    case "$r" in /|/etc|/etc/*|/usr|/usr/*|/var|/var/lib|/var/lib/docker*|/proc*|/sys*|/dev*|/boot*|/run*|/tmp) covered=1 ;; esac
    [[ $covered -eq 0 && -d "$r" ]] && KEEP+=("$r")
  done
  ROOTS=("${KEEP[@]}")

  echo "    Folders mounted into v-panel (same path) and offered for import:"
  if [[ ${#ROOTS[@]} -eq 0 ]]; then echo "      (none)"; else printf '      %s\n' "${ROOTS[@]}"; fi
  if [[ $YES -eq 0 && -t 0 ]]; then
    read -r -p "Continue? [Y/n] " answer
    [[ "$answer" =~ ^[Nn] ]] && { echo "Aborted. Pass the folders explicitly: --adopt /opt/infra /opt/myapp"; exit 1; }
  fi

  {
    echo "# Written by docker-install.sh --adopt"
    echo "VPANEL_SHARED_NETWORK=$NETWORK"
  } > "$ENV_FILE"
  {
    echo "# Written by docker-install.sh --adopt (re-run it with --adopt to regenerate)."
    echo "# Stacks installed before v-panel: mounted at the same path, imported in place from the Import page."
    echo "services:"
    echo "  vpanel:"
    if [[ ${#ROOTS[@]} -gt 0 ]]; then
      echo "    volumes:"
      for r in "${ROOTS[@]}"; do echo "      - \"$r:$r\""; done
    fi
    echo "    environment:"
    echo "      VPanel__SeedInfrastructure: \"false\""
    echo "      VPanel__Defaults__SharedNetwork: \"$NETWORK\""
    i=0
    for r in "${ROOTS[@]}"; do echo "      VPanel__AdoptedRoots__$i: \"$r\""; i=$((i + 1)); done
  } > "$ADOPT_FILE"
  echo "    Wrote $ENV_FILE and $ADOPT_FILE"
fi

ARGS=(-f "$COMPOSE_FILE")
if [[ -f "$ENV_FILE" ]]; then
  ARGS=(--env-file "$ENV_FILE" "${ARGS[@]}")
  NETWORK=$(sed -n 's/^VPANEL_SHARED_NETWORK=//p' "$ENV_FILE" | tail -1)
  NETWORK=${NETWORK:-vpanel-shared}
fi
[[ -f "$ADOPT_FILE" ]] && ARGS+=(-f "$ADOPT_FILE")

if ! docker network inspect "$NETWORK" >/dev/null 2>&1; then
  echo "==> Creating docker network $NETWORK"
  docker network create "$NETWORK" >/dev/null
fi

if [[ $PULL -eq 1 ]]; then
  echo "==> Pulling image"
  docker compose "${ARGS[@]}" pull
  docker compose "${ARGS[@]}" up -d
else
  echo "==> Building image and starting v-panel"
  docker compose "${ARGS[@]}" up -d --build
fi

echo
echo "v-panel is running on 127.0.0.1:5080 (container 'vpanel')."
echo "Open it through an SSH tunnel:  ssh -N -L 5080:127.0.0.1:5080 <user>@<server>   then http://localhost:5080"
echo "Create the admin account on first visit, or from the shell:  docker exec -it vpanel vpanel create-admin"
if [[ -f "$ADOPT_FILE" ]]; then
  echo "Existing stacks: open Import, scan, import the shared infrastructure first, then each app; preview and deploy"
  echo "each imported instance once. Nothing is restarted until you deploy."
else
  echo "Then put CLOUDFLARE_TUNNEL_TOKEN in the .env of 'Shared infrastructure', deploy 'shared-infra'"
  echo "(cloudflared + Caddy + PostgreSQL 18; no web port is opened on this server) and point each Cloudflare"
  echo "public hostname to http://caddy:80. Optionally set Settings > v-panel public URL to reach the panel through"
  echo "the tunnel too (protect it with Cloudflare Access); the SSH tunnel keeps working either way."
fi
echo "Logs: docker logs -f vpanel     Backup: everything lives in $ROOT"
