using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using VPanel.Core.Domain;
using VPanel.Infrastructure.Deployments;
using VPanel.Infrastructure.Import;
using VPanel.Infrastructure.Services;

namespace VPanel.Web.Mcp;

/// <summary>Adopting stacks installed before v-panel (same services as the Import page).</summary>
[McpServerToolType]
public sealed class ImportTools(
    StackScanner scanner,
    StackImportService importer,
    SettingsService settings,
    InstanceService instances,
    DeploymentService deployments,
    AppTypeRegistry registry,
    McpContext ctx)
{
    [McpServerTool(Name = "scan_existing_stacks", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("""
        Read-only scan for docker compose stacks that were installed by hand and are not managed by v-panel yet: the shared
        infrastructure (the stack running Caddy) with the settings v-panel would adopt, app stacks with suggested proxy service/port
        and Public URL (from hand-written Caddy sites), and whether each can be imported (folder visible and inside an adopted root).
        """)]
    public async Task<ScanDto> ScanExistingStacks(CancellationToken ct = default)
    {
        var scan = await scanner.ScanAsync(ct);
        var s = settings.Get();
        return new ScanDto(scan.DockerReachable, scan.Error,
            scan.Infra is { } i
                ? new InfraDto(i.ProjectName, i.SharedNetwork, i.CaddyContainer, i.CaddyRunning, i.CaddyDir, i.CaddyConfigDirInContainer,
                    i.Ingress.ToString(), i.PostgresService is null ? null : $"{i.PostgresService} on {i.PostgresNetwork} ({string.Join(", ", i.PostgresAliases)})", i.Warnings)
                : null,
            scan.InfraManaged,
            scan.Infra is { SharedNetwork: { } net } && net != s.SharedNetwork,
            scan.Stacks.Select(ToDto).ToList(),
            scan.ManualSites.Select(m => new CaddySiteDto(m.Address, m.Upstream, m.File, m.Line, m.TlsInternal, m.CanTakeOver, m.Reason,
                m.Directives, m.ProxyOptions)).ToList(),
            scan.ManagedProjects, s.AdoptedRoots, scan.Warnings);
    }

    [McpServerTool(Name = "import_stack", Destructive = false, OpenWorld = true)]
    [Description("""
        Imports a scanned stack (by compose project name) as an app + instance managed in place: same folder, compose project, volumes and
        networks; nothing is restarted. Import the shared infrastructure first (it sets the shared network and Caddy settings).
        Omitted fields use the scan's suggestions. Then call preview_deploy and run_instance_action("deploy") once to bring it fully under
        management; that deploy also replaces the hand-written Caddy site of the Public URL (take_over_site).
        """)]
    public async Task<ImportResultDto> ImportStack(
        [Description("Compose project name from scan_existing_stacks.")] string project,
        [Description("App name shown in v-panel.")] string? app_name = null,
        [Description("v-panel slug (the compose project name does not change).")] string? slug = null,
        [Description("Public URL; empty string for none. Default: from the hand-written Caddy site.")] string? public_url = null,
        [Description("Service receiving HTTP traffic.")] string? proxy_service = null,
        [Description("Container port of the proxy service.")] int? proxy_port = null,
        [Description("Use Caddy's internal CA (tls internal) for the Public URL.")] bool? tls_internal = null,
        [Description("build | pull_first. Default: pull_first when a service has both image: and build:.")] string? build_mode = null,
        [Description("Replace the hand-written Caddy site serving the Public URL on the first deploy (default true).")] bool take_over_site = true,
        [Description("Caddy directives of the generated site (default: the hand-written site's directives other than reverse_proxy). Empty string for none.")] string? proxy_directives = null,
        [Description("Options inside the generated reverse_proxy block (default: the hand-written site's). Empty string for none.")] string? proxy_options = null,
        CancellationToken ct = default)
    {
        var result = McpContext.Unwrap(await importer.ImportAsync(new ImportRequest
        {
            ProjectName = project,
            AppName = app_name,
            Slug = slug,
            PublicUrl = public_url,
            ProxyService = proxy_service,
            ProxyPort = proxy_port,
            TlsInternal = tls_internal,
            BuildMode = build_mode is null ? null : InstanceTools.ParseBuildMode(build_mode),
            TakeOverSite = take_over_site,
            ProxyDirectives = proxy_directives,
            ProxyOptions = proxy_options,
        }, ctx.User, ct), $"Importing '{project}'");
        var instance = await ctx.RequireInstanceAsync(result.Instance.Id.ToString(), ct);
        return new ImportResultDto(await InstanceTools.DescribeAsync(instance, false, registry, deployments, settings, instances, ct), result.Notes);
    }

    [McpServerTool(Name = "preview_deploy", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists which services the next deploy of an instance would create, recreate, keep or remove (compose config hash vs. running containers). Use it before the first deploy of an imported stack.")]
    public async Task<DeployPreviewDto> PreviewDeploy([Description("Instance id or slug.")] string instance, CancellationToken ct = default)
    {
        var i = await ctx.RequireInstanceAsync(instance, ct);
        var p = await importer.PreviewDeployAsync(i.Id, ct);
        if (!p.Success && p.Services.Count == 0 && p.Error is null) throw new McpException("Preview failed.");
        return new DeployPreviewDto(p.Success, p.Error, p.RecreatesAnything, p.Services.Select(x => new ServiceChangeDto(x.Service, x.Action)).ToList());
    }

    private static ScannedStackDto ToDto(ScannedStack s) => new(s.ProjectName, s.Kind.ToString(), s.WorkingDir, s.ConfigFiles,
        s.RuntimeStatus.ToString(), s.CanImport, s.Accessible, s.InAllowedRoot, s.SuggestedRoot,
        s.Services.Select(x => new ScannedServiceDto(x.Name, x.Image, x.HasBuild,
            x.Networks.Select(n => n.Aliases.Count > 0 ? $"{n.Name} ({string.Join(", ", n.Aliases)})" : n.Name).ToList(), x.ContainerPorts)).ToList(),
        s.SuggestedSlug, s.ProxyService, s.ProxyPort, s.PublicUrl, s.TlsInternal,
        s.SuggestedBuildMode == BuildMode.PullFirst ? "pull_first" : "build",
        s.ManualSite is { } m ? $"{m.Address} ({m.File}:{m.Line})" : null, s.ManualSite?.CanTakeOver ?? false, s.ProxyDirectives, s.ProxyOptions,
        s.TlsNote, s.ConfigError, s.Warnings);
}
