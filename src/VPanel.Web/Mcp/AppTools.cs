using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using VPanel.Core.Domain;
using VPanel.Infrastructure.Services;

namespace VPanel.Web.Mcp;

[McpServerToolType]
public sealed class AppTools(AppService apps, AppTypeRegistry registry, McpContext ctx)
{
    [McpServerTool(Name = "list_apps", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists app templates with their instance counts.")]
    public async Task<List<AppListItem>> ListApps(CancellationToken ct) =>
        (await apps.ListAsync(ct)).Select(s => new AppListItem(s.App.Id, s.App.Slug, s.App.Name, s.App.Description, s.App.IsSystem,
            s.InstanceCount, s.RunningCount, s.App.DefaultProxyService, s.App.DefaultProxyPort)).ToList();

    [McpServerTool(Name = "get_app", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Full definition of an app: compose template, .env (decrypted, may contain secrets), default proxy service/port, volumes and instances.")]
    public async Task<AppDetail> GetApp([Description("App id or slug.")] string app, CancellationToken ct) =>
        AppDetail.From(await ctx.RequireAppAsync(app, ct));

    [McpServerTool(Name = "save_app", Destructive = false, OpenWorld = false)]
    [Description("""
        Creates an app template, or updates one when `app` is given. Saving never deploys: instances pick up template changes on their next deploy.
        When updating, omitted (null) fields keep their current value; pass an empty string to clear env/description/proxy service and 0 to clear the proxy port.
        Returns the saved app and the compose validation report (the app is saved even with warnings; fix errors before deploying).
        """)]
    public async Task<SaveAppResult> SaveApp(
        [Description("Existing app id or slug to update. Omit to create a new app.")] string? app = null,
        [Description("Display name (required when creating).")] string? name = null,
        [Description("URL-safe identifier: lowercase letters, digits, dashes, max 48. Defaults to a slug of the name when creating.")] string? slug = null,
        [Description("Short description, e.g. the source repository URL.")] string? description = null,
        [Description("docker-compose.yml content (required when creating).")] string? compose = null,
        [Description(".env content shared by all instances (KEY=value lines; values may reference ${VPANEL_*}).")] string? env = null,
        [Description("Compose service that receives HTTP traffic from Caddy.")] string? default_proxy_service = null,
        [Description("Container port of that service (1-65535).")] int? default_proxy_port = null,
        CancellationToken ct = default)
    {
        AppDefinition input;
        if (app is null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new McpException("'name' is required when creating an app.");
            if (string.IsNullOrWhiteSpace(compose)) throw new McpException("'compose' is required when creating an app.");
            input = new AppDefinition { Type = AppType.DockerCompose, Slug = slug ?? "" };
        }
        else
        {
            input = await ctx.RequireAppAsync(app, ct);
            if (slug is not null) input.Slug = slug;
        }
        if (name is not null) input.Name = name;
        if (description is not null) input.Description = description;
        if (compose is not null) input.ComposeTemplate = compose;
        if (env is not null) input.EnvTemplate = env;
        if (default_proxy_service is not null) input.DefaultProxyService = default_proxy_service;
        if (default_proxy_port is not null) input.DefaultProxyPort = default_proxy_port == 0 ? null : default_proxy_port;
        input.Instances = [];

        var saved = McpContext.Unwrap(await apps.SaveAsync(input, ctx.User, ct), "Saving the app");
        var detail = await ctx.RequireAppAsync(saved.Id.ToString(), ct);
        return new SaveAppResult(AppDetail.From(detail),
            PanelTools.Check(registry, detail.ComposeTemplate, detail.EnvTemplate, detail.DefaultProxyService));
    }

    [McpServerTool(Name = "delete_app", Destructive = true, OpenWorld = false)]
    [Description("Deletes an app template. Only possible when it has no instances (remove and delete them first).")]
    public async Task<string> DeleteApp([Description("App id or slug.")] string app, CancellationToken ct)
    {
        var a = await ctx.RequireAppAsync(app, ct);
        if (a.IsSystem) throw new McpException($"'{a.Slug}' is the shared infrastructure app; delete it from the web UI if you really mean it.");
        McpContext.Unwrap(await apps.DeleteAsync(a.Id, ctx.User, ct), "Deleting the app");
        return $"App '{a.Slug}' deleted.";
    }
}
