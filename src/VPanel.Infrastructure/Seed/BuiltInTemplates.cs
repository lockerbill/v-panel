namespace VPanel.Infrastructure.Seed;

/// <summary>Templates seeded on first start. Also available as files under samples/.</summary>
public static class BuiltInTemplates
{
    public const string InfraSlug = "shared-infra";

    public const string InfraCompose = """
        # Shared infrastructure managed by v-panel.
        #  - caddy:    reverse proxy + automatic HTTPS. v-panel writes site files into ${VPANEL_CADDY_DIR}/sites
        #              and reloads Caddy on every deploy.
        #  - postgres: shared PostgreSQL. Apps reach it at host "postgres" on the shared network.
        # Both join the external network ${VPANEL_SHARED_NETWORK}; app services proxied by v-panel join it too.
        services:
          caddy:
            image: caddy:2
            container_name: caddy
            restart: unless-stopped
            ports:
              - "80:80"
              - "443:443"
              - "443:443/udp"
            volumes:
              - ${VPANEL_CADDY_DIR}:/etc/caddy:ro
              - caddy_data:/data
              - caddy_config:/config
            extra_hosts:
              - "host.docker.internal:host-gateway"
            networks: [shared]

          postgres:
            image: postgres:17
            container_name: postgres
            restart: unless-stopped
            environment:
              POSTGRES_USER: ${POSTGRES_USER}
              POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
            volumes:
              - postgres_data:/var/lib/postgresql/data
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
