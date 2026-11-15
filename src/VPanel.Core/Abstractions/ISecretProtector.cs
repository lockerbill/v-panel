namespace VPanel.Core.Abstractions;

/// <summary>Encrypts sensitive text (env files) at rest.</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedText);
}

public sealed class PassthroughSecretProtector : ISecretProtector
{
    public static readonly PassthroughSecretProtector Instance = new();
    public string Protect(string plaintext) => plaintext;
    public string Unprotect(string protectedText) => protectedText;
}
