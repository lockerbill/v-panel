using System.Collections.Concurrent;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using VPanel.Core.Domain;
using VPanel.Infrastructure.Data;

namespace VPanel.Infrastructure.Services;

public enum LoginOutcome { Success, InvalidCredentials, LockedOut }

public sealed class UserService(IDbContextFactory<VPanelDbContext> dbFactory, TimeProvider time)
{
    public const int MinPasswordLength = 8;
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, (int Count, DateTimeOffset First)> Failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly PasswordHasher<User> _hasher = new();

    public async Task<bool> AnyUsersAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Users.AnyAsync(ct);
    }

    public async Task<(User? User, string? Error)> CreateAsync(string username, string password, CancellationToken ct = default)
    {
        username = username.Trim();
        if (username.Length is < 3 or > 64) return (null, "Username must be 3-64 characters.");
        if (password.Length < MinPasswordLength) return (null, $"Password must be at least {MinPasswordLength} characters.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Users.AnyAsync(u => u.Username == username, ct)) return (null, "Username already exists.");
        var user = new User { Username = username };
        user.PasswordHash = _hasher.HashPassword(user, password);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return (user, null);
    }

    public async Task<(LoginOutcome Outcome, User? User)> VerifyAsync(string username, string password, string? clientKey, CancellationToken ct = default)
    {
        var key = $"{username.Trim()}|{clientKey}";
        var now = time.GetUtcNow();
        if (Failures.TryGetValue(key, out var f) && f.Count >= MaxFailures && now - f.First < LockoutWindow)
            return (LoginOutcome.LockedOut, null);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username.Trim(), ct);
        var ok = user is not null && _hasher.VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Failed;
        if (!ok)
        {
            Failures.AddOrUpdate(key, (1, now), (_, old) => now - old.First >= LockoutWindow ? (1, now) : (old.Count + 1, old.First));
            return (LoginOutcome.InvalidCredentials, null);
        }
        Failures.TryRemove(key, out _);
        user!.LastLoginAt = now;
        await db.SaveChangesAsync(ct);
        return (LoginOutcome.Success, user);
    }

    public async Task<List<User>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking().OrderBy(u => u.Username).ToListAsync(ct);
    }

    public async Task<bool> ExistsAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Users.AnyAsync(u => u.Id == id, ct);
    }

    /// <summary>Sets a new password without checking the current one (admin action / CLI). Returns an error or null.</summary>
    public async Task<string?> SetPasswordAsync(string username, string newPassword, CancellationToken ct = default)
    {
        if (newPassword.Length < MinPasswordLength) return $"Password must be at least {MinPasswordLength} characters.";
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username.Trim(), ct);
        if (user is null) return $"User '{username.Trim()}' does not exist.";
        user.PasswordHash = _hasher.HashPassword(user, newPassword);
        user.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        // Lift any login lockout for this user so the new password works right away.
        foreach (var key in Failures.Keys.Where(k => k.StartsWith(user.Username + "|", StringComparison.OrdinalIgnoreCase)))
            Failures.TryRemove(key, out _);
        return null;
    }

    /// <summary>Deletes a user. The signed-in user cannot delete themselves, and the last user cannot be deleted.</summary>
    public async Task<(User? Deleted, string? Error)> DeleteAsync(int id, string? currentUsername, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return (null, "User not found.");
        if (string.Equals(user.Username, currentUsername, StringComparison.Ordinal)) return (null, "You cannot delete your own account.");
        if (await db.Users.CountAsync(ct) <= 1) return (null, "Cannot delete the last user.");
        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);
        return (user, null);
    }

    public async Task<string?> ChangePasswordAsync(string username, string current, string newPassword, CancellationToken ct = default)
    {
        if (newPassword.Length < MinPasswordLength) return $"Password must be at least {MinPasswordLength} characters.";
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);
        if (user is null || _hasher.VerifyHashedPassword(user, user.PasswordHash, current) == PasswordVerificationResult.Failed)
            return "Current password is incorrect.";
        user.PasswordHash = _hasher.HashPassword(user, newPassword);
        await db.SaveChangesAsync(ct);
        return null;
    }
}
