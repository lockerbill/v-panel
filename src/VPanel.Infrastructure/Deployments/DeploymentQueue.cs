using System.Threading.Channels;

namespace VPanel.Infrastructure.Deployments;

/// <summary>In-process job queue carrying deployment ids (rows are persisted, so jobs survive restarts).</summary>
public sealed class DeploymentQueue
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });

    public ValueTask EnqueueAsync(int deploymentId, CancellationToken ct = default) => _channel.Writer.WriteAsync(deploymentId, ct);

    public IAsyncEnumerable<int> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
