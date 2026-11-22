using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VPanel.Core.Domain;
using VPanel.Core.Utilities;
using VPanel.Infrastructure.Data;

namespace VPanel.Infrastructure.Services;

/// <summary>A newly created token. <see cref="Secret"/> is only available here: the database keeps a hash.</summary>
public sealed record CreatedApiToken(ApiToken Token, string Secret);

/// <summary>Bearer tokens for agents connecting to the MCP endpoint (<c>/mcp</c>). Every token has full admin rights.</summary>
public sealed partial class ApiTokenService(IDbContextFactory<VPanelDbContext> dbFactory, AuditService audit, TimeProvider time)
{
    public const string SecretPrefix = "vp_";
    private const int PrefixLength = 10;
    private static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex NameRegex();

    public static bool IsValidName(string name) => NameRegex().IsMatch(name);

    public static string Hash(string secret) => Hashing.Sha256(secret);

    public async Task<Result<CreatedApiToken>> CreateAsync(string name, string? createdBy, DateTimeOffset? expiresAt = null, CancellationToken ct = default)
    {
        name = name.Trim();
        if (!IsValidName(name))
            return Result<CreatedApiToken>.Fail("Token name must be 1-64 characters: letters, digits, '.', '_' or '-' (starting with a letter or digit).");
        if (expiresAt is { } e && e <= time.GetUtcNow()) return Result<CreatedApiToken>.Fail("Expiry must be in the future.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.ApiTokens.AnyAsync(t => t.Name == name, ct)) return Result<CreatedApiToken>.Fail($"A token named '{name}' already exists.");

        var secret = SecretPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var token = new ApiToken
        {
            Name = name,
            TokenHash = Hash(secret),
            Prefix = secret[..PrefixLength],
            CreatedBy = createdBy,
            ExpiresAt = expiresAt,
        };
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(createdBy, "token.create", name, expiresAt is null ? null : $"expires {expiresAt:yyyy-MM-dd}", ct);
        return Result<CreatedApiToken>.Ok(new CreatedApiToken(token, secret));
    }

    public async Task<List<ApiToken>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ApiTokens.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
    }

    /// <summary>Deletes a token by id or name; connected agents using it are rejected on their next request.</summary>
    public async Task<Result<ApiToken>> RevokeAsync(string idOrName, string? user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var key = idOrName.Trim();
        var token = int.TryParse(key, out var id)
            ? await db.ApiTokens.FirstOrDefaultAsync(t => t.Id == id, ct)
            : await db.ApiTokens.FirstOrDefaultAsync(t => t.Name == key, ct);
        if (token is null) return Result<ApiToken>.Fail($"Token '{key}' not found.");
        db.ApiTokens.Remove(token);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(user, "token.revoke", token.Name, ct: ct);
        return Result<ApiToken>.Ok(token);
    }

    /// <summary>Returns the token matching <paramref name="secret"/>, or null when unknown or expired.</summary>
    public async Task<ApiToken?> ValidateAsync(string secret, CancellationToken ct = default)
    {
        if (!secret.StartsWith(SecretPrefix, StringComparison.Ordinal) || secret.Length > 128) return null;
        var hash = Hash(secret);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var token = await db.ApiTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        var now = time.GetUtcNow();
        if (token is null || token.IsExpired(now)) return null;
        if (token.LastUsedAt is null || now - token.LastUsedAt.Value >= LastUsedResolution)
        {
            token.LastUsedAt = now;
            await db.SaveChangesAsync(ct);
        }
        return token;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
