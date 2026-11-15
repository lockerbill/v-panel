using Microsoft.AspNetCore.DataProtection;
using VPanel.Core.Abstractions;

namespace VPanel.Infrastructure.Security;

/// <summary>Encrypts secrets with ASP.NET Core Data Protection. Values are prefixed so plaintext stays readable.</summary>
public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private const string Prefix = "dp1:";
    private readonly IDataProtector _protector = provider.CreateProtector("VPanel.Secrets.v1");

    public string Protect(string plaintext) => string.IsNullOrEmpty(plaintext) ? plaintext : Prefix + _protector.Protect(plaintext);

    public string Unprotect(string protectedText)
    {
        if (string.IsNullOrEmpty(protectedText) || !protectedText.StartsWith(Prefix, StringComparison.Ordinal)) return protectedText;
        return _protector.Unprotect(protectedText[Prefix.Length..]);
    }
}
