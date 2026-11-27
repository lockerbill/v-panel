# v-panel

[Tiếng Việt](README.md) | **English**

v-panel is a self-hosted admin panel for managing applications on a VPS. You install v-panel on the server, sign in through the web UI and install other web apps with **docker compose**.

- Define an **app**: name, description, type, and the `docker-compose.yml` and `.env` contents, edited in an IDE-like editor (Monaco).
- **Save draft** only stores the metadata. **Deploy** makes the engine run the real `docker compose`, with the log streamed live on screen.
- One app can run as several **instances** (`procurement-01`, `procurement-02`...). Each instance has its own URL, install folder, `.env` overrides and data (volumes).
- Shared containers (**cloudflared** + **Caddy** + **PostgreSQL 18**) are an app too, managed from v-panel itself. Public traffic goes Cloudflare → Cloudflare Tunnel → Caddy → app, so the server opens no 80/443 ports. On deploy, Caddy is configured automatically from the instance's *Public URL*.
- Deployment history with configuration snapshots, container list, container logs, start/stop/restart/remove and an audit log.
- An **MCP server** (`/mcp`) lets AI agents such as Claude Code or Codex drive the panel with an API token, e.g. install an app straight from a GitHub repository. See [docs/mcp.md](docs/mcp.md).

Stack: .NET 10, Blazor Server, MudBlazor, EF Core + SQLite, YamlDotNet, BlazorMonaco, MCP C# SDK.

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

First access goes through an SSH tunnel (see [Accessing v-panel](#accessing-v-panel)):

```bash
ssh -N -L 5080:127.0.0.1:5080 user@server
```

Open http://localhost:5080 and create the admin account. You can also create it from the shell, see [Users and the admin account](#users-and-the-admin-account).

Then:

1. Create a Cloudflare Tunnel and put its token in `CLOUDFLARE_TUNNEL_TOKEN` in the `.env` tab of the *Shared infrastructure* app, see [Cloudflare Tunnel](#cloudflare-tunnel). The Postgres password lives there too; it is generated randomly.
2. Deploy the **shared-infra** instance to start cloudflared, Caddy and PostgreSQL 18. No container publishes a web port on the host.
3. Create an app, add an instance, fill in its Public URL, add the matching hostname on Cloudflare and press **Deploy**.
4. Optional: go to **Settings → v-panel public URL** and enter e.g. `https://panel.example.com` to reach the panel through the tunnel. Caddy reaches v-panel on the shared network at `vpanel:8080`.

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
> - The container mounts `/var/run/docker.sock` and runs as root. Control over Docker is equivalent to root on the server, so port 5080 is bound to `127.0.0.1` only. If you publish the panel through Cloudflare Tunnel, use a strong password and put the hostname behind Cloudflare Access.
> - `/opt/vpanel` is mounted at the **same path** inside and outside the container. The host's Docker resolves bind-mount paths in app compose files as host paths, so do not change either side of this mount.

### Image on GHCR

The [`.github/workflows/ci.yml`](.github/workflows/ci.yml) workflow runs the tests on every PR. On pushes to `main` or `v*` tags, it builds `linux/amd64` + `linux/arm64` images and pushes them to `ghcr.io/lockerbill/v-panel`:

- a push to `main` produces the `latest` tag;
- a `v1.2.3` tag produces the `1.2.3` and `1.2` tags.

The GHCR package starts out private. Make it public in its *Package settings*, or run `docker login ghcr.io` on the server.

## Accessing v-panel

There are two ways, and both can be used at the same time.

**SSH tunnel** always works, even before shared-infra is deployed or while Caddy/cloudflared are broken. v-panel only listens on `127.0.0.1:5080` on the server:

```bash
ssh -N -L 5080:127.0.0.1:5080 user@server
```

Then open http://localhost:5080. If local port 5080 is taken, change the left side, e.g. `-L 15080:127.0.0.1:5080` and open http://localhost:15080. To avoid retyping it, add to `~/.ssh/config`:

```
Host vpanel
    HostName server.example.com
    User user
    LocalForward 5080 127.0.0.1:5080
```

Then just run `ssh -N vpanel`.

**Public URL through Cloudflare Tunnel:** fill in **Settings → v-panel public URL** (e.g. `https://panel.example.com`) and add that hostname on Cloudflare like any other app. The panel is root-equivalent, so put this hostname behind a Cloudflare Access application (Zero Trust → Access → Applications) so visitors sign in to Cloudflare before reaching the v-panel login form. Blazor uses WebSockets, which Cloudflare Tunnel supports out of the box.

Signing in through both paths does not conflict: the SSH tunnel is `http://localhost` (non-Secure cookie) and the tunnel is `https://panel…` (Secure cookie); each origin keeps its own session.

## Cloudflare Tunnel

Request flow: browser → Cloudflare (HTTPS) → `cloudflared` (a shared-infra container with an outbound connection to Cloudflare) → `http://caddy:80` on the `vpanel-shared` network → app. Caddy publishes no ports, so the server does not open 80/443 and nobody can bypass Cloudflare to reach an app directly.

1. Cloudflare dashboard → **Zero Trust → Networks → Tunnels → Create a tunnel**, choose *Cloudflared*. Copy the token from the install command (the string after `--token`) into `CLOUDFLARE_TUNNEL_TOKEN` in the `.env` of the *Shared infrastructure* app, then deploy **shared-infra**.
2. In the tunnel's **Public Hostname** tab, add each hostname (e.g. `p1.example.com`) with *Service* `HTTP` and URL `caddy:80`. A single wildcard `*.example.com` pointing to `caddy:80` saves declaring every app; create the `*` DNS record (CNAME to `<tunnel-id>.cfargotunnel.com`) yourself if the dashboard does not.
3. **Settings → Public ingress** must be *Cloudflare Tunnel* (the default for new installs).

In this mode v-panel writes Caddy sites as `http://p1.example.com { … }`: TLS ends at Cloudflare and Caddy requests no certificates. For `https://…` Public URLs, Caddy sends `X-Forwarded-Proto: https` to the app so it does not redirect to https in a loop. The visitor's real IP is in the `CF-Connecting-IP` header.

**Direct mode (no Cloudflare):** set **Settings → Public ingress = Direct**, edit the *Shared infrastructure* compose: uncomment Caddy's `ports` block (80, 443) and remove the `cloudflared` service, then deploy. Caddy obtains HTTPS certificates itself as before.

**Moving an existing install (Caddy on 80/443) to the tunnel.** The template stored in v-panel does not change by itself, so do it by hand:

1. **Settings → Public ingress = Cloudflare Tunnel**, then *Save*. v-panel regenerates the Caddy sites as `http://…`.
2. Edit the *Shared infrastructure* compose following [`samples/infra/docker-compose.yml`](samples/infra/docker-compose.yml): remove Caddy's `ports`, add the `cloudflared` service.
3. Add `CLOUDFLARE_TUNNEL_TOKEN=…` to the app's `.env` and declare the hostnames on Cloudflare as above.
4. Deploy **shared-infra**.

To go back to Direct, do the reverse.

## Users and the admin account

- **First visit:** the `/setup` page creates the first admin account. It locks itself once any user exists.
- **Users page** (left menu): list users, create new users, reset passwords and delete users. You cannot delete yourself or the last user. A deleted user is signed out on their next request. Every user is an administrator.
- **Change your own password:** *Settings → Change password*.

To create an admin or reset a password from the server shell (for example before opening the panel, or after losing a password):

```bash
docker exec -it vpanel vpanel create-admin
```

```bash
docker exec -it vpanel vpanel reset-password --username admin
```

The command asks for the username (default `admin`) and the password (hidden). In scripts, pass the password on stdin:

```bash
echo 'a-strong-password' | docker exec -i vpanel vpanel create-admin --username alice --password-stdin
```

Without a terminal (`docker exec` without `-it`), `create-admin` generates a random password and prints it once. For the systemd install use `sudo -u vpanel /opt/vpanel/app/VPanel.Web create-admin`. Run `vpanel help` for all options.

## Driving v-panel from an AI agent (MCP)

v-panel exposes an MCP server at `/mcp` so that Claude Code, Codex and other agents can create apps and instances, deploy and read logs for you. For example, give an agent a GitHub repository and it writes a compose file that follows v-panel's conventions, installs it on the server and iterates until the app runs.

1. Create a token on the **API tokens** page, or from the shell:

   ```bash
   docker exec vpanel vpanel create-token --name claude-laptop
   ```

2. Open an SSH tunnel to the server (`ssh -N -L 5080:127.0.0.1:5080 user@server`) and register the server with the agent, e.g. Claude Code:

   ```bash
   claude mcp add --transport http vpanel http://localhost:5080/mcp --header "Authorization: Bearer vp_xxx"
   ```

3. Ask the agent, e.g. *"Install https://github.com/owner/repo on my server through v-panel, served at https://app.example.com"*.

A token has full admin rights. Every MCP action is recorded in the audit log as `mcp:<token>`. Codex configuration, the tool list and the install workflow are in [docs/mcp.md](docs/mcp.md) (Vietnamese).

## Shared PostgreSQL (version 18)

The **shared-infra** instance runs `postgres:18`. Since version 18 the official image stores data in `/var/lib/postgresql/18/docker`, so the volume is mounted at `/var/lib/postgresql` instead of `/var/lib/postgresql/data`.

**Upgrading an existing install that runs PostgreSQL 17.** The template already stored in v-panel is not changed automatically, because version 18 cannot read version 17 data files. To move to 18:

1. Dump all data:

   ```bash
   docker exec postgres pg_dumpall -U postgres > pg17-dump.sql
   ```

2. In v-panel, open the *Shared infrastructure* app and edit the compose file: `image: postgres:18`, volume `postgres_data:/var/lib/postgresql`.
3. Stop the instance and remove the old volume (you dumped it in step 1):

   ```bash
   docker compose -p vp-shared-infra down
   ```

   ```bash
   docker volume rm vp-shared-infra_postgres_data
   ```

4. **Deploy** the *shared-infra* instance, wait until Postgres is healthy, then restore:

   ```bash
   docker exec -i postgres psql -U postgres < pg17-dump.sql
   ```

## Installing on a server with existing stacks (import)

If the server already runs shared infrastructure (Caddy, Postgres...) and app compose stacks installed by hand, v-panel can manage them **in place**: same folders, same compose project names, no new volumes.

1. Install in adopt mode:

   ```bash
   sudo ./deploy/docker-install.sh --adopt
   ```

   The script finds the running Caddy container and uses its network as the shared network (e.g. `proxy`). From the working folders of the existing compose projects it derives their root folders (`/opt/infra`, `/opt/n8n-arm`, `/opt/<repo>`...), mounts them into the vpanel container at the **same path** and declares them as `VPanel:AdoptedRoots`. To choose the folders yourself, pass them after the flag: `--adopt /opt/infra /opt/apps`. The choices are saved in `/opt/vpanel/vpanel.env` and `/opt/vpanel/docker-compose.adopt.yml` and reused by later upgrades.
2. Open **Import** and press **Scan server**. The scan is read-only and touches nothing. It shows:
   - the shared-infra stack, with the settings v-panel would adopt: network, Caddy container, Caddy folder, ingress mode;
   - the app stacks;
   - the hand-written Caddy sites, with the app each one routes to.
3. Import the **shared infrastructure first**. v-panel updates Settings from it, drops the built-in `shared-infra` template (if it was never deployed) and adds `import …/sites/vp-*.caddy` to the Caddyfile when missing (backed up and validated).
4. Import each app. Proxy service/port, Public URL and `tls internal` are suggested from its hand-written Caddy site. Its containers keep running; the original compose file and `.env` are copied to `<app folder>/.vpanel-backup/`.
5. On each imported instance, press **Preview deploy**: it lists the services that would be recreated. Then press **Deploy** once to bring the stack fully under management. That deploy writes v-panel's files. When the Public URL was served by a simple Caddy site (only `reverse_proxy`, optionally `tls internal`/`encode`), that site is replaced by a `vp-*.caddy` file in the same reload, and the old file is moved to `<Caddy folder>/.vpanel-backup/`.

Notes:

- A service that already has an alias on the shared network (`aliases: [n8n-arm]`) keeps that alias as the route upstream. v-panel generates no `docker-compose.vpanel.yml`, so the container is not recreated.
- Stacks with both `image:` and `build:` should use **Pull first** (suggested on import). It pulls the published images and builds only the missing ones, like `docker compose pull && docker compose up -d`.
- Complex Caddy sites (headers, several upstreams, matchers...) stay hand-written. Leave that instance's Public URL empty, or simplify the site before importing.
- An imported instance that was never deployed can be "Stop managing": v-panel forgets it and leaves its containers and files alone.
- systemd mode: set `VPanel:AdoptedRoots` in `/etc/vpanel/appsettings.json` and give the `vpanel` user read/write access to those folders.

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

v-panel listens on `127.0.0.1:5080` (used by the SSH tunnel) and on the `docker0` bridge IP, so Caddy can proxy to it via `host.docker.internal:5080`. Server-specific configuration lives in `/etc/vpanel/appsettings.json` and `/etc/vpanel/vpanel.env`. Logs: `journalctl -u vpanel -f`.

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
dotnet run --project src/VPanel.Web -- create-admin   # create an admin in the dev data
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
