using Ume.LlmGateway.Domain;

namespace Ume.LlmGateway.Infrastructure.Providers;

public interface IProviderAdapter
{
    bool CanHandle(ProviderType type);
    Task<ProviderResult> SendAsync(ProviderCall call, CancellationToken cancellationToken);
}

/// <summary>
/// The adapter for each provider type, decided once from the registered adapters (the first that can handle a type
/// wins, in registration order), so the request path is an array access.
/// </summary>
public sealed class ProviderAdapterLookup
{
    private readonly IProviderAdapter?[] _byType;

    public ProviderAdapterLookup(IEnumerable<IProviderAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        IProviderAdapter[] registered = [.. adapters];
        _byType = new IProviderAdapter?[Enum.GetValues<ProviderType>().Max(t => (int)t) + 1];
        for (var type = 0; type < _byType.Length; type++)
        {
            _byType[type] = registered.FirstOrDefault(a => a.CanHandle((ProviderType)type));
        }
    }

    public IProviderAdapter Resolve(ProviderType type) =>
        (uint)type < (uint)_byType.Length && _byType[(int)type] is { } adapter
            ? adapter
            : throw new InvalidOperationException($"No provider adapter handles provider type {type}.");
}
