# v-panel

[Tiếng Việt](README.md) | **English**

v-panel is a self-hosted admin panel for managing applications on a VPS. You install v-panel on the server, sign in through the web UI and install other web apps with **docker compose**.

- Define an **app**: name, description, type, and the `docker-compose.yml` and `.env` contents, edited in an IDE-like editor (Monaco).
- **Save draft** only stores the metadata. **Deploy** makes the engine run the real `docker compose`, with the log streamed live on screen.
- One app can run as several **instances** (`procurement-01`, `procurement-02`...). Each instance has its own URL, install folder, `.env` overrides and data (volumes).
- Shared containers (**Caddy** + **PostgreSQL**) are an app too, managed from v-panel itself. On deploy, Caddy is configured automatically from the instance's *Public URL* (with automatic HTTPS).
- Deployment history with configuration snapshots, container list, container logs, start/stop/restart/remove and an audit log.

Stack: .NET 10, Blazor Server, MudBlazor, EF Core + SQLite, YamlDotNet, BlazorMonaco.

## Install with Docker (recommended)

Requirements: Linux (amd64 or arm64) with Docker Engine and the `docker compose` plugin.

```bash
git clone https://github.com/lockerbill/v-panel.git
```

```bash
cd v-panel && sudo ./deploy/docker-install.sh
```

The script:

1. Creates `/opt/vpanel/{apps,caddy,data}`.
2. Creates the Docker network `vpanel-shared`.
3. Builds the image and starts the `vpanel` container from [`deploy/docker-compose.yml`](deploy/docker-compose.yml).

Once the image is published on GHCR, you can skip the build with `sudo ./deploy/docker-install.sh --pull`.

<details>
<summary>Manual install, without the script</summary>

```bash
sudo mkdir -p /opt/vpanel/apps /opt/vpanel/caddy/sites /opt/vpanel/data
```

```bash
sudo docker network create vpanel-shared
```

```bash
sudo docker compose -f deploy/docker-compose.yml up -d --build
```

</details>

First access goes through an SSH tunnel:

```bash
ssh -L 5080:127.0.0.1:5080 user@server
```

Open http://localhost:5080 and create the admin account.

Then:

1. Deploy the **shared-infra** instance to start Caddy (ports 80/443) and PostgreSQL. The Postgres password is generated randomly; view and change it in the `.env` tab of the *Shared infrastructure* app.
2. Go to **Settings → v-panel public URL** and enter e.g. `https://panel.example.com` to reach the panel over HTTPS. Caddy reaches v-panel on the shared network at `vpanel:8080`.
3. Create an app, add an instance, fill in its Public URL and press **Deploy**.

**Upgrade:**

```bash
git pull && sudo docker compose -f deploy/docker-compose.yml up -d --build
```

When using the GHCR image, run `pull` and then `up -d` instead. Running apps are not affected while v-panel restarts.

**Backup:** all data lives in `/opt/vpanel`:

| Folder | Contents |
|---|---|
| `data/` | SQLite database and encryption keys |
| `apps/` | Files of every instance |
| `caddy/` | Caddy configuration |

App data volumes (Postgres...) are Docker volumes and must be backed up separately.

**Logs:** `docker logs -f vpanel`

> **Security and how it works**
> - The container mounts `/var/run/docker.sock` and runs as root. Control over Docker is equivalent to root on the server, so port 5080 is bound to `127.0.0.1` only, and the panel should only be exposed through Caddy (HTTPS) with a strong password.
> - `/opt/vpanel` is mounted at the **same path** inside and outside the container. The host's Docker resolves bind-mount paths in app compose files as host paths, so do not change either side of this mount.

### Image on GHCR

The [`.github/workflows/ci.yml`](.github/workflows/ci.yml) workflow runs the tests on every PR. On pushes to `main` or `v*` tags, it builds `linux/amd64` + `linux/arm64` images and pushes them to `ghcr.io/lockerbill/v-panel`:

- a push to `main` produces the `latest` tag;
- a `v1.2.3` tag produces the `1.2.3` and `1.2` tags.

The GHCR package starts out private. Make it public in its *Package settings*, or run `docker login ghcr.io` on the server.

## Alternative: run with systemd

Use this if you want v-panel to run directly on the host, independent of Docker.

```bash
./scripts/publish.sh            # builds ./publish (self-contained, no .NET install needed on the server)
```

```bash
sudo ./deploy/install.sh ./publish
```

The install script:

- creates the `vpanel` user (in the `docker` group);
- creates `/opt/vpanel/app` (binaries), `/opt/vpanel/apps`, `/opt/vpanel/caddy` and `/var/lib/vpanel` (data);
- installs the `vpanel` systemd service.

v-panel listens on `127.0.0.1:5080` and on the `docker0` bridge IP, so Caddy can proxy to it via `host.docker.internal:5080`. Server-specific configuration lives in `/etc/vpanel/appsettings.json` and `/etc/vpanel/vpanel.env`. Logs: `journalctl -u vpanel -f`.

## Writing docker compose files for multiple instances

Each instance is a separate compose project named `vp-<instance>`. v-panel writes 3 files into the install folder:

| File | Contents |
|---|---|
| `docker-compose.yml` | The app template, or the instance's own compose override |
| `.env` | v-panel system variables, then the app's `.env`, then the instance overrides |
| `docker-compose.vpanel.yml` | Generated: attaches the *proxy service* to the shared network with a unique alias that Caddy calls |

Variables v-panel adds to `.env`, usable in compose files as `${...}`:

| Variable | Example |
|---|---|
| `VPANEL_INSTANCE` | `procurement-01` |
| `VPANEL_PUBLIC_URL`, `VPANEL_HOST` | `https://p1.example.com`, `p1.example.com` |
| `VPANEL_SHARED_NETWORK` | `vpanel-shared` |
| `VPANEL_PROXY_ALIAS` | `vp-procurement-01-web` |
| `COMPOSE_PROJECT_NAME` | `vp-procurement-01` |

These variables come first in the `.env` file, so other values can reference them, e.g. `DB_NAME=procurement_${VPANEL_INSTANCE}`.

**Volumes:**

- *Named volumes* are created by docker compose and prefixed with the project name, so each instance keeps its own data.
- Volumes/networks declared `external: true` are created by v-panel beforehand if missing.
- Relative bind folders (`./data`) are created inside the install folder.
- The *Volumes* tab lets you declare extra external volumes shared between apps.

**Using the shared containers:** declare the external network and reach Postgres at hostname `postgres`.

```yaml
services:
  web:
    image: my/app
    environment:
      DATABASE_URL: postgres://${DB_USER}:${DB_PASSWORD}@postgres:5432/${DB_NAME}
    networks: [default, shared]
networks:
  shared:
    external: true
    name: ${VPANEL_SHARED_NETWORK}
```

A complete example, including creating a separate database per instance, is in [`samples/apps/postgres-app`](samples/apps/postgres-app).

**Public URL:** Caddy routes the instance's `host[/path]` to `<proxy service>:<proxy port>`. The proxy service and port are set on the app (default) or overridden per instance. Both dedicated domains and sub-paths (`https://example.com/p1`) work.

## Development

```bash
dotnet tool restore
```

```bash
dotnet run --project src/VPanel.Web          # http://localhost:5080, data in src/VPanel.Web/.data
```

```bash
dotnet test
```

```bash
docker build -t v-panel:dev .                # build the image
```

```bash
dotnet ef migrations add <Name> -p src/VPanel.Infrastructure -s src/VPanel.Infrastructure -o Data/Migrations
```

Architecture and extension points: [docs/architecture.md](docs/architecture.md) (Vietnamese).
