using System.Text.Json;
using VPanel.Core.Abstractions;
using VPanel.Tests.Engine;

namespace VPanel.Tests.Import;

/// <summary>
/// A server set up by hand before v-panel, modelled on a real one: shared infra stack (Caddy + Postgres, owns the "proxy" and
/// "db" networks, Caddy publishes 80/443 and serves internal names with its local CA), an n8n stack and a cXML stack that join
/// "proxy" with their own aliases. Files live under <see cref="Srv"/>; docker answers come from a <see cref="FakeProcessRunner"/>.
/// </summary>
internal sealed class ArmServer
{
    public string Srv { get; }
    public string Infra => Path.Combine(Srv, "infra");
    public string CaddyDir => Path.Combine(Infra, "caddy");
    public string N8n => Path.Combine(Srv, "n8n-arm");
    public string CxmlRepo => Path.Combine(Srv, "cxml-dev-server");
    public string Cxml => Path.Combine(CxmlRepo, "deploy", "selfhost");

    public const string InfraCompose = """
        # Shared infra stack: one Caddy (reverse proxy for every app) + one PostgreSQL.
        name: infra

        services:
          caddy:
            image: caddy:2.11.9
            restart: unless-stopped
            ports:
              - "80:80"
              - "443:443"
              - "443:443/udp" # HTTP/3
            environment:
              TZ: ${TZ:-Australia/Perth}
            volumes:
              - ./caddy:/etc/caddy:ro
              - caddy_data:/data
              - caddy_config:/config
            networks:
              - proxy

          postgres:
            image: postgres:18.7
            restart: unless-stopped
            shm_size: 256mb
            environment:
              POSTGRES_USER: postgres
              POSTGRES_PASSWORD: "${POSTGRES_PASSWORD:?Set POSTGRES_PASSWORD in .env}"
            volumes:
              - pg_data:/var/lib/postgresql
            networks:
              db:
                aliases:
                  - infra-postgres

        networks:
          proxy:
            name: proxy
            driver: bridge
          db:
            name: db
            driver: bridge

        volumes:
          caddy_data:
          caddy_config:
          pg_data:

        """;

    public const string N8nCompose = """
        # n8n - SQLite, behind the shared Caddy (infra stack).
        name: n8n-arm

        services:
          n8n:
            image: n8nio/n8n:2.48.4
            restart: unless-stopped
            environment:
              N8N_HOST: ${N8N_HOST:?Set N8N_HOST in .env}
              N8N_PORT: "5678"
              N8N_ENCRYPTION_KEY: "${N8N_ENCRYPTION_KEY:?Set N8N_ENCRYPTION_KEY in .env}"
            volumes:
              - ./data:/home/node/.n8n
            networks:
              proxy:
                aliases:
                  - n8n-arm

        networks:
          proxy:
            external: true

        """;

    public const string CxmlCompose = """
        # cXML Dev Server - Caddy now lives in the shared infra stack.
        name: cxml-dev-server

        services:
          app:
            image: ${IMAGE:-ghcr.io/mthpbs/cxml-dev-server:latest}
            build:
              context: ../..
              dockerfile: Dockerfile
            restart: unless-stopped
            volumes:
              - cxml-data:/app/data
            networks:
              cxml:
              proxy:
                aliases:
                  - cxml-app

          backup:
            build: ./backup
            image: cxml-dev-server-backup:local
            restart: unless-stopped
            volumes:
              - cxml-data:/data
              - ./backups:/backups
            network_mode: none

        networks:
          cxml:
            driver: bridge
          proxy:
            external: true

        volumes:
          cxml-data:

        """;

    public const string MainCaddyfile = """
        {
        	email ops@example.com
        }

        import sites/*.caddy

        """;

    public const string N8nSite = """
        # n8n (stack /opt/n8n-arm)
        internal.n8n-arm.arm-app-u01 {
        	tls internal
        	encode zstd gzip
        	reverse_proxy n8n-arm:5678
        }

        """;

    public const string CxmlSite = """
        internal.cxml.arm-app-u01 {
        	tls internal
        	reverse_proxy cxml-app:8080
        	header Strict-Transport-Security "max-age=31536000"
        }

        """;

    public ArmServer(string root)
    {
        Srv = Path.Combine(root, "srv");
        Directory.CreateDirectory(Path.Combine(CaddyDir, "sites"));
        File.WriteAllText(Path.Combine(Infra, "docker-compose.yml"), InfraCompose);
        File.WriteAllText(Path.Combine(Infra, ".env"), "POSTGRES_PASSWORD=secret\n");
        File.WriteAllText(Path.Combine(CaddyDir, "Caddyfile"), MainCaddyfile);
        File.WriteAllText(Path.Combine(CaddyDir, "sites", "n8n.caddy"), N8nSite);
        File.WriteAllText(Path.Combine(CaddyDir, "sites", "cxml.caddy"), CxmlSite);
        Directory.CreateDirectory(Path.Combine(N8n, "data"));
        File.WriteAllText(Path.Combine(N8n, "docker-compose.yml"), N8nCompose);
        File.WriteAllText(Path.Combine(N8n, ".env"), "N8N_HOST=internal.n8n-arm.arm-app-u01\nN8N_ENCRYPTION_KEY=k\n");
        Directory.CreateDirectory(Path.Combine(Cxml, "backup"));
        File.WriteAllText(Path.Combine(Cxml, "docker-compose.yml"), CxmlCompose);
        File.WriteAllText(Path.Combine(Cxml, ".env"), "DOMAIN=internal.cxml.arm-app-u01\n");
    }

    /// <summary>Answers docker the way the real server would (containers, compose ls, resolved models).</summary>
    public void Configure(FakeProcessRunner runner)
    {
        runner
            .Before(a => a is ["ps", "--all", "--quiet", ..], new ProcessResult(0, "c-caddy\nc-pg\nc-n8n\nc-app\nc-backup\n", ""))
            .Before(a => a is ["inspect", ..], new ProcessResult(0, InspectJson(), ""))
            .Before(a => a is ["compose", "ls", ..], new ProcessResult(0, JsonSerializer.Serialize(new[]
            {
                new { Name = "cxml-dev-server", Status = "running(2)", ConfigFiles = Path.Combine(Cxml, "docker-compose.yml") },
                new { Name = "infra", Status = "running(2)", ConfigFiles = Path.Combine(Infra, "docker-compose.yml") },
                new { Name = "n8n-arm", Status = "running(1)", ConfigFiles = Path.Combine(N8n, "docker-compose.yml") },
            }), ""))
            .Before(a => IsConfigJson(a, "infra"), new ProcessResult(0, InfraModel(), ""))
            .Before(a => IsConfigJson(a, "n8n-arm"), new ProcessResult(0, N8nModel(), ""))
            .Before(a => IsConfigJson(a, "cxml-dev-server"), new ProcessResult(0, CxmlModel(), ""));
    }

    public static string? ProjectOf(IReadOnlyList<string> args)
    {
        var i = args.ToList().IndexOf("-p");
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    private static bool IsConfigJson(IReadOnlyList<string> a, string project) =>
        FakeProcessRunner.IsCompose(a, "config") && a.Contains("json") && ProjectOf(a) == project;

    private string InspectJson() => JsonSerializer.Serialize(new object[]
    {
        Container("c-caddy", "infra-caddy-1", "infra", "caddy", "caddy:2.11.9", Infra,
            new Dictionary<string, string[]> { ["proxy"] = ["infra-caddy-1", "caddy"] },
            [new { Type = "bind", Source = CaddyDir, Destination = "/etc/caddy" }, new { Type = "volume", Source = "/var/lib/docker/volumes/infra_caddy_data/_data", Destination = "/data" }],
            ["80/tcp", "443/tcp", "443/udp"]),
        Container("c-pg", "infra-postgres-1", "infra", "postgres", "postgres:18.7", Infra,
            new Dictionary<string, string[]> { ["db"] = ["infra-postgres", "postgres"] }, [], []),
        Container("c-n8n", "n8n-arm-n8n-1", "n8n-arm", "n8n", "n8nio/n8n:2.48.4", N8n,
            new Dictionary<string, string[]> { ["proxy"] = ["n8n-arm", "n8n"] },
            [new { Type = "bind", Source = Path.Combine(N8n, "data"), Destination = "/home/node/.n8n" }], [], hash: "hash-n8n"),
        Container("c-app", "cxml-dev-server-app-1", "cxml-dev-server", "app", "ghcr.io/mthpbs/cxml-dev-server:latest", Cxml,
            new Dictionary<string, string[]> { ["proxy"] = ["cxml-app", "app"], ["cxml-dev-server_cxml"] = ["app"] }, [], []),
        Container("c-backup", "cxml-dev-server-backup-1", "cxml-dev-server", "backup", "cxml-dev-server-backup:local", Cxml,
            new Dictionary<string, string[]>(), [], []),
    });

    private static object Container(string id, string name, string project, string service, string image, string dir,
        Dictionary<string, string[]> networks, object[] mounts, string[] published, string hash = "h") => new
    {
        Id = id + new string('0', 40),
        Name = "/" + name,
        State = new { Status = "running", ExitCode = 0 },
        Config = new
        {
            Image = image,
            Labels = new Dictionary<string, string>
            {
                ["com.docker.compose.project"] = project,
                ["com.docker.compose.service"] = service,
                ["com.docker.compose.project.working_dir"] = dir,
                ["com.docker.compose.project.config_files"] = Path.Combine(dir, "docker-compose.yml"),
                ["com.docker.compose.config-hash"] = hash,
                ["com.docker.compose.oneoff"] = "False",
            },
        },
        NetworkSettings = new { Networks = networks.ToDictionary(n => n.Key, n => new { Aliases = n.Value, DNSNames = new[] { name } }) },
        Mounts = mounts,
        HostConfig = new { PortBindings = published.ToDictionary(p => p, _ => new[] { new { HostIp = "", HostPort = "1" } }) },
    };

    private string InfraModel() => JsonSerializer.Serialize(new
    {
        name = "infra",
        services = new Dictionary<string, object>
        {
            ["caddy"] = new
            {
                image = "caddy:2.11.9",
                networks = new Dictionary<string, object?> { ["proxy"] = null },
                ports = new object[] { new { target = 80, published = "80" }, new { target = 443, published = "443" } },
                volumes = new object[] { new { type = "bind", source = CaddyDir, target = "/etc/caddy", read_only = true }, new { type = "volume", source = "caddy_data", target = "/data" } },
            },
            ["postgres"] = new
            {
                image = "postgres:18.7",
                networks = new Dictionary<string, object?> { ["db"] = new { aliases = new[] { "infra-postgres" } } },
            },
        },
        networks = new { proxy = new { name = "proxy", driver = "bridge" }, db = new { name = "db", driver = "bridge" } },
    });

    private string N8nModel() => JsonSerializer.Serialize(new
    {
        name = "n8n-arm",
        services = new Dictionary<string, object>
        {
            ["n8n"] = new
            {
                image = "n8nio/n8n:2.48.4",
                networks = new Dictionary<string, object?> { ["proxy"] = new { aliases = new[] { "n8n-arm" } } },
                volumes = new object[] { new { type = "bind", source = Path.Combine(N8n, "data"), target = "/home/node/.n8n" } },
            },
        },
        networks = new { proxy = new { name = "proxy", external = true } },
    });

    private string CxmlModel() => JsonSerializer.Serialize(new
    {
        name = "cxml-dev-server",
        services = new Dictionary<string, object>
        {
            ["app"] = new
            {
                image = "ghcr.io/mthpbs/cxml-dev-server:latest",
                build = new { context = CxmlRepo, dockerfile = "Dockerfile" },
                networks = new Dictionary<string, object?> { ["cxml"] = null, ["proxy"] = new { aliases = new[] { "cxml-app" } } },
            },
            ["backup"] = new
            {
                image = "cxml-dev-server-backup:local",
                build = new { context = Path.Combine(Cxml, "backup") },
                network_mode = "none",
            },
        },
        networks = new { cxml = new { name = "cxml-dev-server_cxml" }, proxy = new { name = "proxy", external = true } },
    });
}
