using VPanel.Core.Abstractions;
using VPanel.Core.Domain;

namespace VPanel.Infrastructure.Services;

/// <summary>Resolves the engine for an app type. New types register an extra <see cref="IAppTypeHandler"/>.</summary>
public sealed class AppTypeRegistry(IEnumerable<IAppTypeHandler> handlers)
{
    private readonly Dictionary<AppType, IAppTypeHandler> _handlers = handlers.ToDictionary(h => h.Type);

    public IReadOnlyCollection<AppType> SupportedTypes => _handlers.Keys;

    public IAppTypeHandler Get(AppType type) =>
        _handlers.TryGetValue(type, out var h) ? h : throw new NotSupportedException($"No handler registered for app type {type}.");
}
