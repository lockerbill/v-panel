using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using VPanel.Core.Abstractions;
using VPanel.Core.Domain;
using VPanel.Infrastructure.Deployments;
using VPanel.Infrastructure.Docker;
using VPanel.Infrastructure.Services;

namespace VPanel.Web.Mcp;

[McpServerToolType]
public sealed class InstanceTools(
    InstanceService instances,
    DeploymentService deployments,
    DeploymentLogBus logBus,
    AppTypeRegistry registry,
    SettingsService settings,
    McpContext ctx)
{
    private const int MaxWaitSeconds = 600;
    private const int DefaultWaitSeconds = 120;
    private const int MaxLogLines = 2000;

    internal static InstanceListItem ToListItem(InstanceSummary s) => new(s.Instance.Id, s.Instance.Slug, s.Instance.Name,
        s.Instance.App?.Slug ?? "", s.Instance.Status.ToString(), s.Instance.StatusDetail, s.Instance.PublicUrl, s.HasUndeployedChanges,
        s.Instance.LastDeployedAt);

    [McpServerTool(Name = "list_instances", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists instances (deployments of apps) with status, Public URL and whether saved changes are not deployed yet.")]
    public async Task<List<InstanceListItem>> ListInstances([Description("Only instances of this app (id or slug).")] string? app = null,
        CancellationToken ct = default)
    {
        int? appId = app is null ? null : (await ctx.RequireAppAsync(app, ct)).Id;
        return (await instances.ListAsync(appId, ct)).Select(ToListItem).ToList();
    }

    [McpServerTool(Name = "get_instance", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Details of an instance: status, URL, install folder, compose project, proxy alias, env overrides, live containers and recent deployments.")]
    public async Task<InstanceDetail> GetInstance(
        [Description("Instance id or slug.")] string instance,
        [Description("Query docker for the instance's containers.")] bool include_containers = true,
        CancellationToken ct = default)
    {
        var i = await ctx.RequireInstanceAsync(instance, ct);
        return await DescribeAsync(i, include_containers, ct);
    }

    [McpServerTool(Name = "save_instance", Destructive = false, OpenWorld = false)]
    [Description("""
        Creates an instance of an app (pass `app`, omit `instance`) or updates one (pass `instance`). Saving never deploys; call run_instance_action with "deploy" afterwards.
        The install folder is <apps root>/<slug>. When updating, omitted (null) fields keep their value; pass an empty string to clear text fields and 0 to clear the proxy port.
        """)]
    public async Task<InstanceDetail> SaveInstance(
        [Description("App id or slug (required when creating).")] string? app = null,
        [Description("Existing instance id or slug to update. Omit to create.")] string? instance = null,
        [Description("Display name. Defaults to the slug.")] string? name = null,
        [Description("Instance slug (compose project vp-<slug>); defaults to <app>-01, <app>-02... Cannot change after deploy.")] string? slug = null,
        [Description("Public URL routed by Caddy, e.g. https://myapp.example.com or https://example.com/myapp. Empty for no web route.")] string? public_url = null,
        [Description(".env lines overriding the app .env for this instance only.")] string? env_overrides = null,
        [Description("Full docker-compose.yml replacing the app template for this instance only (rarely needed; empty string reverts to the template).")] string? compose_override = null,
        [Description("Override of the app's proxy service.")] string? proxy_service = null,
        [Description("Override of the app's proxy port.")] int? proxy_port = null,
        [Description("Description.")] string? description = null,
        CancellationToken ct = default)
    {
        AppInstance input;
        if (instance is null)
        {
            if (app is null) throw new McpException("Pass 'app' to create an instance, or 'instance' to update one.");
            var a = await ctx.RequireAppAsync(app, ct);
            input = await instances.NewDraftAsync(a.Id, ct);
            if (slug is not null)
            {
                input.Slug = slug;
                input.InstallPath = ""; // re-derived from the slug
                if (name is null) input.Name = slug;
            }
        }
        else
        {
            input = await ctx.RequireInstanceAsync(instance, ct);
            if (app is not null && !string.Equals(app, input.App!.Slug, StringComparison.OrdinalIgnoreCase) && app != input.AppId.ToString())
                throw new McpException("An instance cannot be moved to another app.");
            if (slug is not null && slug != input.Slug)
            {
                // keep the folder in sync with the slug when it was the default one
                if (input.InstallPath.TrimEnd('/') == Path.Combine(settings.Get().AppsRoot, input.Slug)) input.InstallPath = "";
                input.Slug = slug;
            }
        }
        if (name is not null) input.Name = name;
        if (public_url is not null) input.PublicUrl = public_url;
        if (env_overrides is not null) input.EnvOverrides = env_overrides;
        if (compose_override is not null) input.ComposeOverride = compose_override;
        if (proxy_service is not null) input.ProxyService = proxy_service;
        if (proxy_port is not null) input.ProxyPort = proxy_port == 0 ? null : proxy_port;
        if (description is not null) input.Description = description;

        var saved = McpContext.Unwrap(await instances.SaveAsync(input, ctx.User, ct), "Saving the instance");
        return await DescribeAsync(await ctx.RequireInstanceAsync(saved.Id.ToString(), ct), includeContainers: false, ct);
    }

    [McpServerTool(Name = "run_instance_action", Destructive = false, OpenWorld = true)]
    [Description("""
        Queues a job for an instance and optionally waits for it: "deploy" (write files, pull/build, docker compose up, update Caddy routes),
        "start", "stop", "restart", or "down" (remove containers, keep volumes). Returns the job status and the tail of its log.
        If the job is still running when wait_seconds elapses, poll get_deployment with the returned id.
        """)]
    public async Task<DeploymentDetail> RunInstanceAction(
        [Description("Instance id or slug.")] string instance,
        [Description("deploy | start | stop | restart | down")] string action,
        [Description("Seconds to wait for the job to finish (0-600, default 120).")] int wait_seconds = DefaultWaitSeconds,
        [Description("Log lines to return (default 80).")] int log_tail_lines = 80,
        CancellationToken ct = default)
    {
        var parsed = action.Trim().ToLowerInvariant() switch
        {
            "deploy" => DeploymentAction.Deploy,
            "start" => DeploymentAction.Start,
            "stop" => DeploymentAction.Stop,
            "restart" => DeploymentAction.Restart,
            "down" => DeploymentAction.Down,
            "remove" => throw new McpException("Use remove_instance to remove an instance."),
            _ => throw new McpException($"Unknown action '{action}'. Use deploy, start, stop, restart or down."),
        };
        var i = await ctx.RequireInstanceAsync(instance, ct);
        var job = McpContext.Unwrap(await deployments.EnqueueAsync(i.Id, parsed, ctx.User, ct: ct), $"Queuing {action}");
        return await WaitAsync(job.Id, wait_seconds, log_tail_lines, ct);
    }

    [McpServerTool(Name = "remove_instance", Destructive = true, OpenWorld = true)]
    [Description("Stops and removes the instance's containers (docker compose down) and its Caddy route. Optionally deletes its named volumes (DATA LOSS) and install folder. The instance record stays (status Removed) and can be deployed again or deleted with delete_instance.")]
    public async Task<DeploymentDetail> RemoveInstance(
        [Description("Instance id or slug.")] string instance,
        [Description("Also delete the instance's named volumes - irreversible data loss.")] bool delete_volumes = false,
        [Description("Also delete the install folder.")] bool delete_files = false,
        [Description("Seconds to wait for the job to finish (0-600, default 120).")] int wait_seconds = DefaultWaitSeconds,
        CancellationToken ct = default)
    {
        var i = await ctx.RequireInstanceAsync(instance, ct);
        var job = McpContext.Unwrap(await deployments.EnqueueAsync(i.Id, DeploymentAction.Remove, ctx.User,
            new RemoveOptions(delete_volumes, delete_files), ct), "Queuing remove");
        return await WaitAsync(job.Id, wait_seconds, 80, ct);
    }

    [McpServerTool(Name = "delete_instance", Destructive = true, OpenWorld = false)]
    [Description("Deletes an instance record and its deployment history. Only allowed for instances that were never deployed or were removed with remove_instance.")]
    public async Task<string> DeleteInstance([Description("Instance id or slug.")] string instance, CancellationToken ct)
    {
        var i = await ctx.RequireInstanceAsync(instance, ct);
        McpContext.Unwrap(await instances.DeleteAsync(i.Id, ctx.User, ct), "Deleting the instance");
        return $"Instance '{i.Slug}' deleted.";
    }

    [McpServerTool(Name = "list_deployments", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Recent deployment jobs (newest first), optionally for one instance.")]
    public async Task<List<DeploymentListItem>> ListDeployments(
        [Description("Instance id or slug.")] string? instance = null,
        [Description("Max jobs to return (1-100, default 20).")] int limit = 20,
        CancellationToken ct = default)
    {
        int? id = instance is null ? null : (await ctx.RequireInstanceAsync(instance, ct)).Id;
        return (await deployments.ListAsync(id, Math.Clamp(limit, 1, 100), ct)).Select(DeploymentListItem.From).ToList();
    }

    [McpServerTool(Name = "get_deployment", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Status and log of a deployment job (live log while it runs). Use wait_seconds to wait for a running job.")]
    public Task<DeploymentDetail> GetDeployment(
        [Description("Deployment id.")] int deployment_id,
        [Description("Log lines to return from the end (default 200, max 2000).")] int log_tail_lines = 200,
        [Description("Seconds to wait for the job to finish (0-600, default 0).")] int wait_seconds = 0,
        CancellationToken ct = default) =>
        WaitAsync(deployment_id, wait_seconds, log_tail_lines, ct);

    [McpServerTool(Name = "get_container_logs", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Last lines of the instance's container logs (docker compose logs, all services, with timestamps). Lines start with the container name, so filter by service there.")]
    public async Task<string> GetContainerLogs(
        [Description("Instance id or slug.")] string instance,
        [Description("Lines per container (default 200, max 2000).")] int tail = 200,
        CancellationToken ct = default)
    {
        var i = await ctx.RequireInstanceAsync(instance, ct);
        var lines = new List<string>();
        var gate = new object();
        await registry.Get(i.App!.Type).StreamLogsAsync(i, settings.Get(), Math.Clamp(tail, 1, MaxLogLines), follow: false,
            line => { lock (gate) lines.Add(line); }, ct);
        return lines.Count == 0 ? "(no log output)" : string.Join('\n', lines);
    }

    private async Task<InstanceDetail> DescribeAsync(AppInstance i, bool includeContainers, CancellationToken ct)
    {
        IReadOnlyList<ContainerDto>? containers = null;
        if (includeContainers && i.DeployedConfigHash is not null)
        {
            var list = await registry.Get(i.App!.Type).GetContainersAsync(i, settings.Get(), ct);
            containers = list.Select(c => new ContainerDto(c.Name, c.Service, c.State, c.Status, c.Image, c.Ports)).ToList();
        }
        var recent = (await deployments.ListAsync(i.Id, 5, ct)).Select(DeploymentListItem.From).ToList();
        var proxyService = i.EffectiveProxyService;
        return new InstanceDetail(i.Id, i.Slug, i.Name, i.Description, i.App!.Name, i.App.Slug, i.Status.ToString(), i.StatusDetail,
            i.PublicUrl, i.InstallPath, i.ProjectName, i.ProxyService, i.ProxyPort, proxyService, i.EffectiveProxyPort,
            proxyService is null ? null : ComposeRenderer.ProxyAlias(i, proxyService), i.EnvOverrides, i.ComposeOverride,
            instances.HasUndeployedChanges(i), i.LastDeployedAt, containers, recent);
    }

    private async Task<DeploymentDetail> WaitAsync(int deploymentId, int waitSeconds, int logTail, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(waitSeconds, 0, MaxWaitSeconds));
        while (true)
        {
            var d = await deployments.GetAsync(deploymentId, ct) ?? throw new McpException($"Deployment {deploymentId} not found.");
            var finished = d.Status is not (DeploymentStatus.Queued or DeploymentStatus.Running);
            if (finished || DateTimeOffset.UtcNow >= deadline) return Describe(d, finished, logTail);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    private DeploymentDetail Describe(Deployment d, bool finished, int logTail)
    {
        var log = finished ? d.Log : logBus.TryGet(d.Id)?.Text ?? d.Log;
        var lines = log.TrimEnd('\n').Split('\n');
        var take = Math.Clamp(logTail, 0, MaxLogLines);
        var tail = lines.Length <= take ? log.TrimEnd('\n') : $"... ({lines.Length - take} earlier lines)\n" + string.Join('\n', lines[^take..]);
        string? hint = null;
        if (!finished) hint = $"Job is still {d.Status.ToString().ToLowerInvariant()}; call get_deployment(deployment_id: {d.Id}, wait_seconds: 120).";
        else if (d.Status == DeploymentStatus.Failed && d.ErrorMessage?.Contains("proxy configuration failed", StringComparison.Ordinal) == true)
            hint = "The app containers are up but the Caddy route could not be applied: make sure the shared-infra instance (Caddy) is deployed and running (get_instance shared-infra), then deploy this instance again.";
        else if (d.Status == DeploymentStatus.Failed) hint = "Read the log above, then get_container_logs for runtime errors; fix the app/instance and deploy again.";
        else if (d.Action == DeploymentAction.Deploy) hint = "Containers are up. Check get_instance (status, containers) and get_container_logs to confirm the app actually started.";
        return new DeploymentDetail(d.Id, d.Instance?.Slug ?? d.InstanceId.ToString(), d.Action.ToString(), d.Status.ToString(), finished,
            d.ErrorMessage, d.TriggeredBy, d.CreatedAt, d.StartedAt, d.FinishedAt, d.Instance?.Status.ToString(), d.Instance?.PublicUrl,
            tail, hint);
    }
}
