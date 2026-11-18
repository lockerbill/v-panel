using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using MudBlazor.Services;
using Serilog;
using Serilog.Events;
using VPanel.Infrastructure;
using VPanel.Infrastructure.Services;
using VPanel.Infrastructure.Seed;
using VPanel.Web.Auth;
using VPanel.Web.Cli;
using VPanel.Web.Components;

// `vpanel create-admin`, `vpanel reset-password`...: run a maintenance command instead of the web host.
var command = AdminCommands.IsCommand(args) ? AdminCommands.Parse(args) : null;
if (command is not null && AdminCommands.IsStandalone(command))
{
    Environment.ExitCode = AdminCommands.RunStandalone(command, new CliConsole());
    return;
}

// Commands may be run from any folder (e.g. `sudo -u vpanel /opt/vpanel/app/VPanel.Web create-admin`): fall back to the
// install folder so the bundled appsettings.json is found, exactly like the service (WorkingDirectory=/opt/vpanel/app).
var contentRoot = command is not null && !File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"))
    ? AppContext.BaseDirectory
    : null;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = command is null ? args : [], ContentRootPath = contentRoot });

// Server-specific overrides live outside the install folder so upgrades don't overwrite them.
builder.Configuration.AddJsonFile(Environment.GetEnvironmentVariable("VPANEL_CONFIG") ?? "/etc/vpanel/appsettings.json", optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();

// Default to loopback only; override with ASPNETCORE_URLS (see deploy/install.sh) or "Urls" in config.
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://127.0.0.1:5080");

if (command is null)
{
    builder.Host.UseSystemd();
    builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration).Enrich.FromLogContext().WriteTo.Console());
}
else
{
    // Commands only print their own output; warnings and errors go to stderr.
    builder.Host.UseSerilog((_, cfg) => cfg.MinimumLevel.Warning()
        .MinimumLevel.Override("Microsoft.AspNetCore.DataProtection", LogEventLevel.Error)
        .WriteTo.Console(standardErrorFromLevel: LogEventLevel.Verbose));
}

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices();

builder.Services.AddVPanelInfrastructure(builder.Configuration, builder.Environment.ContentRootPath);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "vpanel.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.LoginPath = "/login";
        o.LogoutPath = "/logout";
        o.AccessDeniedPath = "/login";
        o.ExpireTimeSpan = TimeSpan.FromHours(12);
        o.SlidingExpiration = true;
        // Sign out sessions of users that were deleted on the Users page.
        o.Events.OnValidatePrincipal = async ctx =>
        {
            var id = ctx.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            var users = ctx.HttpContext.RequestServices.GetRequiredService<UserService>();
            if (!int.TryParse(id, out var userId) || !await users.ExistsAsync(userId, ctx.HttpContext.RequestAborted))
            {
                ctx.RejectPrincipal();
                await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<CurrentUser>();

// v-panel normally sits behind the shared Caddy container
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

await VPanelInitializer.InitializeAsync(app.Services);

if (command is not null)
{
    Environment.ExitCode = await AdminCommands.RunAsync(app.Services, command, new CliConsole());
    return;
}

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapPost("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.LocalRedirect("/login");
});
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
