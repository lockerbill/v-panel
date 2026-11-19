namespace VPanel.Infrastructure.Seed;

/// <summary>Templates seeded on first start. Also available as files under samples/.</summary>
public static class BuiltInTemplates
{
    public const string InfraSlug = "shared-infra";

    public const string InfraCompose = """
        # Shared infrastructure managed by v-panel.
        #  - cloudflared: Cloudflare Tunnel. Public traffic goes Cloudflare -> cloudflared -> http://caddy:80 -> app,
        #                 so no web port is opened on the server. Put the tunnel token in .env (CLOUDFLARE_TUNNEL_TOKEN) and,
        #                 in the Cloudflare dashboard, point each public hostname (or *.example.com) to HTTP caddy:80.
        #  - caddy:       reverse proxy. v-panel writes site files into ${VPANEL_CADDY_DIR}/sites and reloads Caddy on every deploy.
        #                 With Settings > Public ingress = Cloudflare Tunnel, sites are plain HTTP (TLS ends at Cloudflare).
        #  - postgres:    shared PostgreSQL 18. Apps reach it at host "postgres" on the shared network.
        #                 PG18 keeps its data under /var/lib/postgresql/18/docker, so the volume is mounted at /var/lib/postgresql.
        # All join the external network ${VPANEL_SHARED_NETWORK}; app services proxied by v-panel join it too.
        #
        # Direct mode instead of the tunnel (Settings > Public ingress = Direct, Caddy obtains HTTPS certificates itself):
        # uncomment the caddy "ports" block below and remove the cloudflared service.
        services:
          cloudflared:
            image: cloudflare/cloudflared:latest
            container_name: cloudflared
            restart: unless-stopped
            command: tunnel --no-autoupdate run
            environment:
              TUNNEL_TOKEN: ${CLOUDFLARE_TUNNEL_TOKEN}
            depends_on: [caddy]
            networks: [shared]

          caddy:
            image: caddy:2
            container_name: caddy
            restart: unless-stopped
            # Direct mode only:
            # ports:
            #   - "80:80"
            #   - "443:443"
            #   - "443:443/udp"
            volumes:
              - ${VPANEL_CADDY_DIR}:/etc/caddy:ro
              - caddy_data:/data
              - caddy_config:/config
            extra_hosts:
              - "host.docker.internal:host-gateway"
            networks: [shared]

          postgres:
            image: postgres:18
            container_name: postgres
            restart: unless-stopped
            environment:
              POSTGRES_USER: ${POSTGRES_USER}
              POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
            volumes:
              - postgres_data:/var/lib/postgresql
            healthcheck:
              test: ["CMD-SHELL", "pg_isready -U ${POSTGRES_USER}"]
              interval: 10s
              timeout: 5s
              retries: 5
            networks: [shared]

        volumes:
          caddy_data:
          caddy_config:
          postgres_data:

        networks:
          shared:
            external: true
            name: ${VPANEL_SHARED_NETWORK}

        """;

    public static string InfraEnv(string postgresPassword) => $"""
        # Shared PostgreSQL superuser (apps should use their own database/user)
        POSTGRES_USER=postgres
        POSTGRES_PASSWORD={postgresPassword}

        # Cloudflare Tunnel token: Cloudflare Zero Trust > Networks > Tunnels > Create a tunnel (cloudflared),
        # copy the token from the install command. Not needed in Direct mode.
        CLOUDFLARE_TUNNEL_TOKEN=

        """;

    public const string WhoamiCompose = """
        # Example app: a tiny HTTP server that echoes request info.
        # Each instance gets its own compose project (vp-<instance>), so the same template can be deployed many times.
        services:
          web:
            image: traefik/whoami
            restart: unless-stopped
            environment:
              WHOAMI_NAME: ${VPANEL_INSTANCE}
            volumes:
              - data:/data

        volumes:
          data:

        """;

    public const string WhoamiEnv = """
        # Variables available in docker-compose.yml as ${NAME}.
        # v-panel also injects: VPANEL_INSTANCE, VPANEL_PUBLIC_URL, VPANEL_HOST, VPANEL_SHARED_NETWORK, ...
        GREETING=hello

        """;
}
