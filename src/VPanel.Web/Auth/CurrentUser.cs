using Microsoft.AspNetCore.Components.Authorization;

namespace VPanel.Web.Auth;

/// <summary>Resolves the signed-in username inside a Blazor circuit.</summary>
public sealed class CurrentUser(AuthenticationStateProvider provider)
{
    public async Task<string?> NameAsync() => (await provider.GetAuthenticationStateAsync()).User.Identity?.Name;
}
