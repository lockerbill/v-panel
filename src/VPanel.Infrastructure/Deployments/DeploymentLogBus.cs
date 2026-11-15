using System.Collections.Concurrent;
using System.Text;
using VPanel.Core.Abstractions;

namespace VPanel.Infrastructure.Deployments;

/// <summary>Log of a running deployment: accumulates text and notifies live subscribers (UI).</summary>
public sealed class LiveDeploymentLog(int deploymentId, TimeProvider time) : IDeploymentLog
{
    private readonly StringBuilder _text = new();
    private readonly object _gate = new();

    public int DeploymentId { get; } = deploymentId;
    public event Action<string>? LineAdded;
    public event Action? Completed;
    public bool IsCompleted { get; private set; }

    public string Text { get { lock (_gate) return _text.ToString(); } }

    public void Info(string message) => Append($"[{Stamp()}] ▶ {message}");
    public void Warn(string message) => Append($"[{Stamp()}] ⚠ {message}");
    public void Error(string message) => Append($"[{Stamp()}] ✖ {message}");
    public void Output(string line) => Append(line);

    private string Stamp() => time.GetLocalNow().ToString("HH:mm:ss");

    private void Append(string line)
    {
        lock (_gate) _text.Append(line).Append('\n');
        LineAdded?.Invoke(line);
    }

    internal void Complete()
    {
        IsCompleted = true;
        Completed?.Invoke();
    }
}

/// <summary>Registry of in-flight deployment logs so the UI can attach to a running job.</summary>
public sealed class DeploymentLogBus(TimeProvider time)
{
    private readonly ConcurrentDictionary<int, LiveDeploymentLog> _live = new();

    /// <summary>Raised when any deployment starts or finishes (for list/dashboard refresh).</summary>
    public event Action<int>? DeploymentChanged;

    public LiveDeploymentLog Start(int deploymentId)
    {
        var log = new LiveDeploymentLog(deploymentId, time);
        _live[deploymentId] = log;
        DeploymentChanged?.Invoke(deploymentId);
        return log;
    }

    public LiveDeploymentLog? TryGet(int deploymentId) => _live.GetValueOrDefault(deploymentId);

    public void Complete(int deploymentId)
    {
        if (_live.TryRemove(deploymentId, out var log)) log.Complete();
        DeploymentChanged?.Invoke(deploymentId);
    }

    public void NotifyChanged(int deploymentId) => DeploymentChanged?.Invoke(deploymentId);
}
