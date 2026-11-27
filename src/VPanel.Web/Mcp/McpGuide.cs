namespace VPanel.Web.Mcp;

/// <summary>Text given to agents: the short server instructions (sent on connect) and the full install guide (a tool and a prompt).</summary>
public static class McpGuide
{
    public const string Instructions = """
        v-panel manages docker compose apps on this server. An *app* is a template (docker-compose.yml + .env); each
        *instance* is one deployment of it (compose project `vp-<instance>`, own folder, own .env overrides, own volumes,
        optional Public URL routed by the shared Caddy). Changes are saved as drafts; nothing runs until an instance is
        deployed with run_instance_action.

        Before installing or changing an app, call get_install_guide once: it explains the compose conventions v-panel
        expects (no published ports, proxy service/port, VPANEL_* variables, shared network, shared Postgres) and the
        workflow validate_compose -> save_app -> save_instance -> run_instance_action(deploy) -> get_deployment /
        get_container_logs. Apps and instances can be referenced by numeric id or slug.

        Stacks installed by hand before v-panel (a shared Caddy/Postgres stack, app compose stacks) are adopted with
        scan_existing_stacks -> import_stack (infrastructure first) -> preview_deploy -> run_instance_action(deploy).
        Imported instances keep their folder and compose project name (not `vp-<slug>`).

        Every tool acts with full administrator rights and is recorded in the audit log under this token's name.
        Ask the user before destructive actions (remove_instance with delete_volumes/delete_files, delete_app,
        delete_instance, changing the system app shared-infra).
        """;

    public const string InstallGuide = """
        # Installing an app on v-panel

        ## Model
        - **App** = template: `docker-compose.yml` + `.env` + default *proxy service/port* (the service and container port
          that receive HTTP traffic). Saving an app never runs anything.
        - **Instance** = one deployment of an app: slug (compose project `vp-<slug>`), install folder
          `<AppsRoot>/<slug>`, optional *Public URL*, `.env` overrides, optional full compose override, optional proxy
          service/port override. Several instances of one app are independent (own containers, named volumes, data).
        - Deploy pipeline: v-panel renders `docker-compose.yml`, `.env` (managed variables first, then app env, then
          instance overrides) and `docker-compose.vpanel.yml` (attaches the proxy service to the shared network with the
          alias `vp-<instance>-<service>`), runs `docker compose config`, creates external networks/volumes and relative
          bind folders, `pull --ignore-buildable`, `up -d --remove-orphans` (`--build` when a service has `build:`),
          then updates the Caddy routes.
        - Public traffic: Cloudflare -> cloudflared -> Caddy (`shared-infra` instance) -> `vp-<instance>-<service>:<port>`
          (or Caddy on 80/443 in Direct ingress mode). get_panel_info tells you the ingress mode.

        ## Step 1 - study the repository
        Find out, from the repo you were given:
        1. How it is packaged: an image already published (README, GitHub Packages/Docker Hub, release workflow), a
           `Dockerfile`, or an upstream `docker-compose.yml` / `compose.yaml` to adapt.
        2. The HTTP port the web process listens on inside the container and whether it honours a base path
           (sub-path Public URLs like `https://example.com/app` need that; a dedicated hostname is safer).
        3. Required environment variables and secrets, database/cache needs (Postgres, Redis...), persistent paths.
        4. Whether it must know its own external URL (use `${VPANEL_PUBLIC_URL}` / `${VPANEL_HOST}`), and whether it
           sits behind a TLS-terminating proxy (Caddy sends `X-Forwarded-Proto: https` for https Public URLs).

        ## Step 2 - choose how the image is obtained
        a. **Published image**: `image: ghcr.io/owner/app:1.2.3` (pin a version tag).
        b. **Public repo with a Dockerfile**: let Docker build straight from git, no files needed on the server:
           ```yaml
           build:
             context: https://github.com/owner/repo.git#main   # or #v1.2.3, #<commit>, #main:subdir
             dockerfile: Dockerfile
           image: local/<app>:main                             # optional tag for the built image
           ```
        c. **Private repo, local changes, or a slow build**: over SSH on the server, clone into the instance folder
           (after save_instance, the folder is `<AppsRoot>/<instance>`; v-panel only writes `docker-compose.yml`, `.env`
           and `docker-compose.vpanel.yml` there and leaves other files alone):
           `git clone <repo> <AppsRoot>/<instance>/src`, then use `build: ./src` (relative to the install folder).
           Alternatively `docker build -t local/<app>:<tag> .` on the server and use `image: local/<app>:<tag>`
           (`pull` then reports a warning for that image, which is fine).

        ## Step 3 - write the compose file for v-panel
        Rules:
        - **Do not publish ports** (`ports:`) for web services: Caddy reaches the proxy service on the shared network.
          Use `expose:` or nothing. Publishing host ports also breaks running several instances.
        - **No `container_name:`** - container names must be unique per instance; compose derives them from the project.
        - Declare the HTTP service + container port as the app's `default_proxy_service` / `default_proxy_port`.
        - Use **named volumes** for data (`volumes: { data: {} }`): compose prefixes them with `vp-<instance>`, so every
          instance gets its own data. Relative binds (`./config`) live in the install folder.
        - Put configuration and secrets in the app `.env` and reference them as `${NAME}`; generate strong random
          secrets yourself (e.g. `openssl rand -hex 32` over SSH). Per-instance values go in the instance env overrides.
        - Managed variables available in compose and in .env values (see get_panel_info): `VPANEL_INSTANCE`,
          `VPANEL_INSTANCE_NAME`, `VPANEL_APP`, `VPANEL_PUBLIC_URL`, `VPANEL_HOST`, `VPANEL_SHARED_NETWORK`,
          `VPANEL_INSTALL_PATH`, `VPANEL_PROXY_ALIAS`, `COMPOSE_PROJECT_NAME`.
        - `restart: unless-stopped` on long-running services; add a `healthcheck` when the image supports one.
        - Omit the obsolete top-level `version:` key.

        **Own database inside the app** (simplest, fully isolated): add a `db` service (e.g. `postgres:18` with volume
        `db_data:/var/lib/postgresql`) on the default network; the app reaches it as `db:5432`.

        **Shared PostgreSQL** (`postgres` container of the `shared-infra` instance): join the external shared network and
        create a database/role per instance with a one-shot init service:
        ```yaml
        services:
          db-init:
            image: postgres:18
            restart: "no"
            environment:
              PGHOST: postgres
              PGUSER: ${POSTGRES_ADMIN_USER}
              PGPASSWORD: ${POSTGRES_ADMIN_PASSWORD}
              DB_NAME: ${DB_NAME}
              DB_USER: ${DB_USER}
              DB_PASSWORD: ${DB_PASSWORD}
            entrypoint: ["/bin/sh", "-c"]
            command:
              - |
                until pg_isready -q; do sleep 1; done
                psql -v ON_ERROR_STOP=1 -tc "SELECT 1 FROM pg_roles WHERE rolname = '$$DB_USER'" | grep -q 1 \
                  || psql -v ON_ERROR_STOP=1 -c "CREATE ROLE \"$$DB_USER\" LOGIN PASSWORD '$$DB_PASSWORD'"
                psql -v ON_ERROR_STOP=1 -tc "SELECT 1 FROM pg_database WHERE datname = '$$DB_NAME'" | grep -q 1 \
                  || psql -v ON_ERROR_STOP=1 -c "CREATE DATABASE \"$$DB_NAME\" OWNER \"$$DB_USER\""
            networks: [shared]
          web:
            image: my/app:1.0
            restart: unless-stopped
            depends_on:
              db-init:
                condition: service_completed_successfully
            environment:
              DATABASE_URL: postgres://${DB_USER}:${DB_PASSWORD}@postgres:5432/${DB_NAME}
            networks: [default, shared]
        networks:
          shared:
            external: true
            name: ${VPANEL_SHARED_NETWORK}
        ```
        with app `.env`:
        ```
        POSTGRES_ADMIN_USER=postgres
        POSTGRES_ADMIN_PASSWORD=<POSTGRES_PASSWORD from the shared-infra app env: get_app shared-infra>
        DB_NAME=app_${VPANEL_INSTANCE}
        DB_USER=app_${VPANEL_INSTANCE}
        DB_PASSWORD=<random>
        ```
        Note: `$$` escapes `$` for compose. Only the proxy service is attached to the shared network automatically; any
        other service that must reach `postgres` needs `networks: [default, shared]` as above.

        ## Step 4 - save, deploy, verify
        1. `validate_compose(compose, env)` until there are no errors; read the warnings.
        2. `save_app(name, compose, env, default_proxy_service, default_proxy_port, description)`. Pick a short slug;
           to change an existing app pass `app`.
        3. `save_instance(app, public_url?)` - the Public URL is the address users open (e.g. `https://myapp.example.com`).
           Leave it empty for a background service with no web UI.
        4. If you chose option 2c, clone/build over SSH now (the install folder is returned by save_instance).
        5. `run_instance_action(instance, "deploy", wait_seconds)` (builds can take minutes; if it returns before the job
           ends, poll get_deployment). On failure read the log tail in the result, then `get_container_logs`, fix the app
           or instance, and deploy again. A deploy that succeeds can still leave a crashing container: check
           `get_instance` (status Running, containers healthy) and the container logs.
        6. Verify over SSH if useful. In tunnel mode Caddy publishes no ports, so test from the shared network:
           `docker run --rm --network <shared network> curlimages/curl -sI http://<proxy alias>:<port>/` (the app itself)
           and `docker run --rm --network <shared network> curlimages/curl -sI -H 'Host: <host>' http://caddy/` (the route).
           get_instance returns the proxy alias.
        7. Tell the user what to do outside v-panel: in Cloudflare Tunnel mode add the Public URL hostname to the tunnel
           (Service `HTTP`, URL `caddy:80`) unless a wildcard exists; in Direct mode point DNS to the server.
           Report the instance URL, where secrets were stored, and anything you could not verify.

        ## Changing a running app
        Edit with save_app / save_instance, then deploy again; get_instance shows `hasUndeployedChanges`. Do not edit the
        rendered files in the install folder by hand - v-panel overwrites them on every deploy. Changing an app template
        affects every instance of that app on its next deploy.

        ## Adopting stacks installed by hand
        1. `scan_existing_stacks`: unmanaged compose projects, the shared infrastructure (stack running Caddy) and the
           settings v-panel would adopt, hand-written Caddy sites and which stack each one routes to. A stack can be imported
           when `canImport` is true; otherwise its folder must be mounted / added to the adopted roots (`suggestedRoot`,
           installer `--adopt`): tell the user, do not work around it.
        2. `import_stack` the infrastructure first (`importInfraFirst`), then each app. Nothing is restarted; the instance
           keeps its folder, compose project, named volumes and networks. A service already on the shared network with an
           alias keeps it as the route upstream (no override file, no recreate). A hand-written Caddy site with one
           `reverse_proxy host:port` (no handle/route/redir/matchers) is taken over with its other directives
           (`proxyDirectives`, e.g. `import common`, `header {...}`) and reverse_proxy options (`proxyOptions`, e.g. health
           checks) kept verbatim; edit them later with save_instance. `build_mode: pull_first` keeps the usual
           `docker compose pull && up -d` behaviour for services with both `image:` and `build:`. `tls_internal` for
           internal hostnames served with Caddy's local CA.
        3. `preview_deploy` on the instance: services marked `recreate` will restart on the first deploy (data is kept).
           Ask the user before deploying when anything would be recreated.
        4. `run_instance_action(deploy)` once. It writes v-panel's files (the import already copied the original compose
           file and .env to `.vpanel-backup/` in the folder) and, when the Public URL was served by a hand-written Caddy
           site that can be taken over, replaces that site with a generated one in the same Caddy reload.
        `delete_instance` on an imported instance that was never deployed only forgets it (containers untouched).

        ## Lifecycle actions
        `start`, `stop`, `restart`, `down` (stop and remove containers, keep data), `remove_instance` (down + optionally
        delete named volumes and the install folder), `delete_instance` (forget a removed or never-deployed instance),
        `delete_app` (only when it has no instances). Ask the user before deleting data.
        """;

    public static string InstallPrompt(string repoUrl, string? publicUrl, string? instanceName)
    {
        var target = string.IsNullOrWhiteSpace(publicUrl)
            ? "Ask me for the Public URL (hostname) if the app has a web UI."
            : $"Serve it at {publicUrl.Trim()}.";
        var name = string.IsNullOrWhiteSpace(instanceName) ? "" : $" Name the instance '{instanceName.Trim()}'.";
        return $"""
            Install the application from {repoUrl.Trim()} on my server and let v-panel manage it.{name} {target}

            1. Call get_install_guide and get_panel_info first.
            2. Study the repository (packaging, HTTP port, env vars, database, persistent data) and pick how to get the image.
            3. Write a docker compose file that follows the v-panel conventions, validate it with validate_compose,
               then create the app with save_app and an instance with save_instance.
            4. Deploy with run_instance_action and iterate on failures using the deployment log and container logs.
               Use SSH to the server only for what v-panel cannot do (cloning a private repo, building an image, checks).
            5. Finish with a short report: app and instance slugs, URL, secrets you generated and where they are stored,
               DNS/Cloudflare steps I must do, and anything that is not verified.
            """;
    }
}
