using ModelContextProtocol;
using VPanel.Core.Domain;
using VPanel.Infrastructure.Services;

namespace VPanel.Web.Mcp;

/// <summary>Shared helpers for MCP tools: who is calling (for the audit log) and id-or-slug lookups.</summary>
public sealed class McpContext(IHttpContextAccessor http, AppService apps, InstanceService instances)
{
    /// <summary>Audit user name, e.g. <c>mcp:claude-laptop</c>.</summary>
    public string User => http.HttpContext?.User.Identity?.Name ?? "mcp";

    public async Task<AppDefinition> RequireAppAsync(string idOrSlug, CancellationToken ct)
    {
        var app = await FindAppAsync(idOrSlug, ct);
        return app ?? throw new McpException($"App '{idOrSlug}' not found. Use list_apps to see existing apps.");
    }

    public async Task<AppDefinition?> FindAppAsync(string idOrSlug, CancellationToken ct)
    {
        var key = idOrSlug.Trim();
        if (int.TryParse(key, out var id)) return await apps.GetAsync(id, ct);
        var match = (await apps.ListAsync(ct)).FirstOrDefault(a => string.Equals(a.App.Slug, key, StringComparison.OrdinalIgnoreCase));
        return match is null ? null : await apps.GetAsync(match.App.Id, ct);
    }

    public async Task<AppInstance> RequireInstanceAsync(string idOrSlug, CancellationToken ct)
    {
        var instance = await FindInstanceAsync(idOrSlug, ct);
        return instance ?? throw new McpException($"Instance '{idOrSlug}' not found. Use list_instances to see existing instances.");
    }

    public async Task<AppInstance?> FindInstanceAsync(string idOrSlug, CancellationToken ct)
    {
        var key = idOrSlug.Trim();
        if (int.TryParse(key, out var id)) return await instances.GetAsync(id, ct);
        var match = (await instances.ListAsync(ct: ct)).FirstOrDefault(i => string.Equals(i.Instance.Slug, key, StringComparison.OrdinalIgnoreCase));
        return match?.Instance;
    }

    /// <summary>Turns a failed service result into a tool error the agent can read and act on.</summary>
    public static T Unwrap<T>(Result<T> result, string action) =>
        result.Success ? result.Value! : throw new McpException($"{action} failed: {string.Join(" ", result.Errors)}");
}
