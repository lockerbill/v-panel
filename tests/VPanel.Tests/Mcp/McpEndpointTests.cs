using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using VPanel.Core.Abstractions;
using VPanel.Infrastructure.Services;
using VPanel.Tests.Engine;

namespace VPanel.Tests.Mcp;

/// <summary>The real web host with docker replaced by a fake runner, driven through an MCP client like an agent would.</summary>
public sealed class McpEndpointTests : IAsyncLifetime
{
    private PanelFactory _factory = null!;
    private string _secret = "";

    public async Task InitializeAsync()
    {
        _factory = new PanelFactory();
        _ = _factory.Server; // start the host (migrations, seed, background workers)
        var created = await _factory.Services.GetRequiredService<ApiTokenService>().CreateAsync("test-agent", "tester");
        _secret = created.Value!.Secret;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<McpClient> ConnectAsync(string? secret = null)
    {
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret ?? _secret);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }, http, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    private static async Task<JsonElement> CallAsync(McpClient client, string tool, Dictionary<string, object?> args)
    {
        var result = await client.CallToolAsync(tool, args);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        Assert.False(result.IsError == true, $"{tool} failed: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> CallErrorAsync(McpClient client, string tool, Dictionary<string, object?> args)
    {
        var result = await client.CallToolAsync(tool, args);
        Assert.True(result.IsError);
        return Assert.IsType<TextContentBlock>(result.Content[0]).Text;
    }

    [Fact]
    public async Task Requests_without_a_valid_token_are_rejected()
    {
        using var http = _factory.CreateClient();
        var body = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json");
        var response = await http.PostAsync("/mcp", body);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);

        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = body };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "vp_wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task A_token_does_not_open_the_web_ui()
    {
        using var http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _secret);
        var response = await http.GetAsync("/apps");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Lists_tools_prompt_and_instructions()
    {
        await using var client = await ConnectAsync();
        Assert.Contains("get_install_guide", client.ServerInstructions);
        var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToHashSet();
        foreach (var name in new[] { "get_panel_info", "get_install_guide", "validate_compose", "list_apps", "get_app", "save_app", "delete_app",
                     "list_instances", "get_instance", "save_instance", "run_instance_action", "remove_instance", "delete_instance",
                     "list_deployments", "get_deployment", "get_container_logs", "scan_existing_stacks", "import_stack", "preview_deploy" })
            Assert.Contains(name, tools);

        var prompt = await client.GetPromptAsync("install_app_from_repo",
            new Dictionary<string, object?> { ["repo_url"] = "https://github.com/acme/notes", ["public_url"] = "https://notes.example.com" });
        var text = Assert.IsType<TextContentBlock>(prompt.Messages.Single().Content).Text;
        Assert.Contains("https://github.com/acme/notes", text);
        Assert.Contains("https://notes.example.com", text);

        var info = await CallAsync(client, "get_panel_info", []);
        Assert.Equal(_factory.AppsRoot, info.GetProperty("appsRoot").GetString());
        Assert.Contains(info.GetProperty("systemInstances").EnumerateArray(), i => i.GetProperty("slug").GetString() == "shared-infra");
    }

    [Fact]
    public async Task Validate_compose_flags_published_ports_and_container_name()
    {
        await using var client = await ConnectAsync();
        var report = await CallAsync(client, "validate_compose", new()
        {
            ["compose"] = "services:\n  web:\n    image: nginx\n    container_name: web\n    ports: [\"8080:80\"]\n",
            ["proxy_service"] = "api",
        });
        Assert.True(report.GetProperty("valid").GetBoolean());
        var warnings = string.Join("\n", report.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()));
        Assert.Contains("publishes host ports", warnings);
        Assert.Contains("container_name", warnings);
        Assert.Contains("'api' is not defined", warnings);

        var broken = await CallAsync(client, "validate_compose", new() { ["compose"] = "services:\n  web:\n    restart: always\n" });
        Assert.False(broken.GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task Installs_an_app_end_to_end_and_records_the_token_in_the_audit_log()
    {
        await using var client = await ConnectAsync();

        var error = await CallErrorAsync(client, "save_app", new() { ["name"] = "Notes" });
        Assert.Contains("'compose' is required", error);

        var saved = await CallAsync(client, "save_app", new()
        {
            ["name"] = "Notes",
            ["slug"] = "notes",
            ["description"] = "https://github.com/acme/notes",
            ["compose"] = "services:\n  web:\n    build:\n      context: https://github.com/acme/notes.git#main\n    volumes: [data:/data]\nvolumes:\n  data:\n",
            ["env"] = "SECRET_KEY=abc",
            ["default_proxy_service"] = "web",
            ["default_proxy_port"] = 3000,
        });
        Assert.Equal("notes", saved.GetProperty("app").GetProperty("slug").GetString());
        Assert.True(saved.GetProperty("validation").GetProperty("valid").GetBoolean());

        // partial update keeps the other fields
        var updated = await CallAsync(client, "save_app", new() { ["app"] = "notes", ["env"] = "SECRET_KEY=def" });
        Assert.Equal(3000, updated.GetProperty("app").GetProperty("defaultProxyPort").GetInt32());
        Assert.Contains("build:", updated.GetProperty("app").GetProperty("compose").GetString());

        var instance = await CallAsync(client, "save_instance", new() { ["app"] = "notes", ["public_url"] = "https://notes.example.com" });
        Assert.Equal("notes-01", instance.GetProperty("slug").GetString());
        Assert.Equal("vp-notes-01-web", instance.GetProperty("proxyAlias").GetString());
        Assert.Equal(Path.Combine(_factory.AppsRoot, "notes-01"), instance.GetProperty("installPath").GetString());

        var clash = await CallErrorAsync(client, "save_instance", new() { ["app"] = "notes", ["slug"] = "notes-02", ["public_url"] = "https://notes.example.com" });
        Assert.Contains("already used", clash);

        var job = await CallAsync(client, "run_instance_action", new() { ["instance"] = "notes-01", ["action"] = "deploy", ["wait_seconds"] = 30 });
        Assert.Equal("Succeeded", job.GetProperty("status").GetString());
        Assert.True(job.GetProperty("finished").GetBoolean());
        Assert.Equal("mcp:test-agent", job.GetProperty("triggeredBy").GetString());
        Assert.Contains("docker compose up", job.GetProperty("logTail").GetString());
        Assert.Contains(_factory.Runner.Calls, c => c.Arguments.Contains("up") && c.Arguments.Contains("--build"));

        var detail = await CallAsync(client, "get_instance", new() { ["instance"] = "notes-01" });
        Assert.Equal("Running", detail.GetProperty("status").GetString());
        Assert.False(detail.GetProperty("hasUndeployedChanges").GetBoolean());

        var deployment = await CallAsync(client, "get_deployment", new() { ["deployment_id"] = job.GetProperty("id").GetInt32() });
        Assert.Equal("Succeeded", deployment.GetProperty("status").GetString());

        var unknown = await CallErrorAsync(client, "run_instance_action", new() { ["instance"] = "notes-01", ["action"] = "explode" });
        Assert.Contains("Unknown action", unknown);

        var removed = await CallAsync(client, "remove_instance", new() { ["instance"] = "notes-01", ["wait_seconds"] = 30 });
        Assert.Equal("Succeeded", removed.GetProperty("status").GetString());
        await CallAsync(client, "list_instances", new() { ["app"] = "notes" });
        Assert.Contains("deleted", (await client.CallToolAsync("delete_instance", new Dictionary<string, object?> { ["instance"] = "notes-01" }))
            .Content.OfType<TextContentBlock>().Single().Text);
        Assert.Contains("deleted", (await client.CallToolAsync("delete_app", new Dictionary<string, object?> { ["app"] = "notes" }))
            .Content.OfType<TextContentBlock>().Single().Text);

        var audit = await _factory.Services.GetRequiredService<AuditService>().RecentAsync(50);
        Assert.Contains(audit, a => a is { User: "mcp:test-agent", Action: "app.create", Target: "notes" });
        Assert.Contains(audit, a => a is { User: "mcp:test-agent", Action: "instance.deploy", Target: "notes-01" });
    }

    [Fact]
    public async Task Scan_reports_no_stacks_on_a_fresh_server_and_import_explains_why()
    {
        await using var client = await ConnectAsync();
        var scan = await CallAsync(client, "scan_existing_stacks", []);
        Assert.True(scan.GetProperty("dockerReachable").GetBoolean());
        Assert.Empty(scan.GetProperty("stacks").EnumerateArray());
        var error = await CallErrorAsync(client, "import_stack", new() { ["project"] = "nope" });
        Assert.Contains("No unmanaged compose project named 'nope'", error);
    }

    [Fact]
    public async Task System_app_cannot_be_deleted_over_mcp()
    {
        await using var client = await ConnectAsync();
        var error = await CallErrorAsync(client, "delete_app", new() { ["app"] = "shared-infra" });
        Assert.Contains("shared infrastructure", error);
    }

    [Fact]
    public async Task Revoked_token_stops_working()
    {
        var tokens = _factory.Services.GetRequiredService<ApiTokenService>();
        var other = (await tokens.CreateAsync("short-lived", null)).Value!;
        await using (var client = await ConnectAsync(other.Secret))
            await CallAsync(client, "list_apps", []);
        await tokens.RevokeAsync("short-lived", "tester");
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(other.Secret));
    }

    private sealed class PanelFactory : WebApplicationFactory<Program>
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("vpanel-mcp-").FullName;
        public FakeProcessRunner Runner { get; } = new FakeProcessRunner()
            .On(a => FakeProcessRunner.IsCompose(a, "config"), new ProcessResult(0, "{\"services\":{\"web\":{\"build\":{\"context\":\"x\"}}}}", ""))
            .On(a => FakeProcessRunner.IsCompose(a, "ps"), new ProcessResult(0, "{\"Name\":\"c1\",\"State\":\"running\"}", ""));

        public string AppsRoot => Path.Combine(Root, "apps");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("VPanel:DataDir", Path.Combine(Root, "data"));
            builder.UseSetting("VPanel:Defaults:AppsRoot", AppsRoot);
            builder.UseSetting("VPanel:Defaults:CaddyDir", Path.Combine(Root, "caddy"));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IProcessRunner>();
                services.AddSingleton<IProcessRunner>(Runner);
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root, true); } catch { /* best effort */ }
        }
    }
}
