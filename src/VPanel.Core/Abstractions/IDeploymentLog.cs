namespace VPanel.Core.Abstractions;

/// <summary>Sink for deployment output; implementations persist and stream lines to the UI.</summary>
public interface IDeploymentLog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
    /// <summary>Raw output line from a process.</summary>
    void Output(string line);
}

public sealed class NullDeploymentLog : IDeploymentLog
{
    public static readonly NullDeploymentLog Instance = new();
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message) { }
    public void Output(string line) { }
}
