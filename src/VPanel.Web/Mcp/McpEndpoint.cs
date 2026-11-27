using System.Reflection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using ModelContextProtocol.Protocol;
using VPanel.Web.Auth;

namespace VPanel.Web.Mcp;

/// <summary>
/// MCP server (Streamable HTTP, stateless) at <see cref="Path"/>, so agents such as Claude Code or Codex can drive v-panel.
/// Requests must carry an API token (<c>Authorization: Bearer vp_...</c>) created on the API tokens page or with
/// <c>vpanel create-token</c>.
/// </summary>
public static class McpEndpoint
{
    public const string Path = "/mcp";

    public static IServiceCollection AddVPanelMcp(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<McpContext>();
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(ApiTokenAuthenticationHandler.SchemeName, null);
        services.AddAuthorizationBuilder()
            .AddPolicy(ApiTokenAuthenticationHandler.PolicyName, p => p
                .AddAuthenticationSchemes(ApiTokenAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser());

        services.AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation
                {
                    Name = "v-panel",
                    Title = "v-panel",
                    Version = typeof(McpEndpoint).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev",
                };
                o.ServerInstructions = McpGuide.Instructions;
            })
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<PanelTools>()
            .WithTools<AppTools>()
            .WithTools<InstanceTools>()
            .WithTools<ImportTools>()
            .WithPrompts<InstallPrompts>();
        return services;
    }

    /// <summary>Keeps the HTML "not found" page from replacing empty 401/405 responses of the MCP endpoint.</summary>
    public static IApplicationBuilder UseMcpWithoutStatusCodePages(this IApplicationBuilder app) =>
        app.Use((ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments(Path) && ctx.Features.Get<IStatusCodePagesFeature>() is { } feature)
                feature.Enabled = false;
            return next();
        });

    public static IEndpointConventionBuilder MapVPanelMcp(this IEndpointRouteBuilder app) =>
        app.MapMcp(Path).RequireAuthorization(ApiTokenAuthenticationHandler.PolicyName);
}
