using System.Security.Cryptography;
using System.Text;

namespace VPanel.Core.Utilities;

public static class Hashing
{
    public static string Sha256(params string?[] parts)
    {
        using var sha = SHA256.Create();
        var sb = new StringBuilder();
        foreach (var p in parts) sb.Append(p ?? "").Append('\u0001');
        return Convert.ToHexStringLower(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
