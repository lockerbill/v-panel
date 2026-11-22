using VPanel.Infrastructure.Services;

namespace VPanel.Tests.Services;

public class ApiTokenServiceTests
{
    [Fact]
    public async Task Created_token_validates_and_only_its_hash_is_stored()
    {
        await using var host = await ServiceTestHost.CreateAsync();
        var tokens = host.Get<ApiTokenService>();

        var created = await tokens.CreateAsync("claude-laptop", "admin");
        Assert.True(created.Success);
        var secret = created.Value!.Secret;
        Assert.StartsWith(ApiTokenService.SecretPrefix, secret);
        Assert.True(secret.Length > 40);

        var stored = Assert.Single(await tokens.ListAsync());
        Assert.NotEqual(secret, stored.TokenHash);
        Assert.Equal(ApiTokenService.Hash(secret), stored.TokenHash);
        Assert.StartsWith(stored.Prefix, secret, StringComparison.Ordinal);
        Assert.Null(stored.LastUsedAt);

        var valid = await tokens.ValidateAsync(secret);
        Assert.Equal("claude-laptop", valid?.Name);
        Assert.NotNull((await tokens.ListAsync()).Single().LastUsedAt);

        Assert.Null(await tokens.ValidateAsync(secret + "x"));
        Assert.Null(await tokens.ValidateAsync("not-a-token"));
    }

    [Fact]
    public async Task Names_are_validated_and_unique()
    {
        await using var host = await ServiceTestHost.CreateAsync();
        var tokens = host.Get<ApiTokenService>();
        Assert.True((await tokens.CreateAsync("codex", null)).Success);
        Assert.False((await tokens.CreateAsync("CODEX", null)).Success);
        Assert.False((await tokens.CreateAsync("has space", null)).Success);
        Assert.False((await tokens.CreateAsync("", null)).Success);
        Assert.False((await tokens.CreateAsync("past", null, DateTimeOffset.UtcNow.AddDays(-1))).Success);
    }

    [Fact]
    public async Task Revoked_and_expired_tokens_are_rejected()
    {
        await using var host = await ServiceTestHost.CreateAsync();
        var tokens = host.Get<ApiTokenService>();
        var a = (await tokens.CreateAsync("a", null)).Value!;
        var b = (await tokens.CreateAsync("b", null, DateTimeOffset.UtcNow.AddMilliseconds(300))).Value!;

        Assert.True((await tokens.RevokeAsync("a", "admin")).Success);
        Assert.Null(await tokens.ValidateAsync(a.Secret));
        Assert.False((await tokens.RevokeAsync("a", "admin")).Success);

        Assert.NotNull(await tokens.ValidateAsync(b.Secret));
        await Task.Delay(400);
        Assert.Null(await tokens.ValidateAsync(b.Secret));

        Assert.True((await tokens.RevokeAsync(b.Token.Id.ToString(), "admin")).Success);
        Assert.Empty(await tokens.ListAsync());
    }
}
