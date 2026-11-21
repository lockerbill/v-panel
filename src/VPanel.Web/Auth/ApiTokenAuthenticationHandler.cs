using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using VPanel.Infrastructure.Services;

namespace VPanel.Web.Auth;

/// <summary>
/// <c>Authorization: Bearer vp_...</c> authentication for the MCP endpoint. Only used by the <see cref="PolicyName"/> policy:
/// the UI keeps using the cookie scheme, so a token never opens the web UI and a cookie never opens <c>/mcp</c>.
/// </summary>
public sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiTokenService tokens) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiToken";
    public const string PolicyName = "mcp";

    /// <summary>Prefix of the user name recorded in the audit log for actions made with a token.</summary>
    public const string UserPrefix = "mcp:";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var secret = header["Bearer ".Length..].Trim();
        var token = await tokens.ValidateAsync(secret, Context.RequestAborted);
        if (token is null) return AuthenticateResult.Fail("Invalid or expired API token.");

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, token.Id.ToString()),
            new Claim(ClaimTypes.Name, UserPrefix + token.Name),
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer realm=\"v-panel\"";
        return Task.CompletedTask;
    }
}
