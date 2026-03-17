using Microsoft.Extensions.Logging;
using Modular.Core.Metadata;
using Modular.Core.Versioning;

namespace Modular.Core.Dependencies;

/// <summary>
/// Aggregates multiple backend-specific version providers, routing requests
/// based on the canonical ID prefix (e.g., "nexusmods:", "gamebanana:").
/// Falls back to trying all providers if no prefix matches.
/// </summary>
public class AggregateVersionProvider : IModVersionProvider
{
    private readonly Dictionary<string, IModVersionProvider> _providers = new(
        StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<AggregateVersionProvider>? _logger;

    public AggregateVersionProvider(ILogger<AggregateVersionProvider>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Registers a version provider for a specific backend prefix.
    /// </summary>
    public void Register(string backendId, IModVersionProvider provider)
    {
        _providers[backendId] = provider;
    }

    public async Task<List<SemanticVersion>> GetAvailableVersionsAsync(
        string canonicalId,
        CancellationToken ct = default)
    {
        var provider = ResolveProvider(canonicalId);
        if (provider != null)
            return await provider.GetAvailableVersionsAsync(canonicalId, ct);

        // No matching provider found — try all providers
        foreach (var kvp in _providers)
        {
            try
            {
                var versions = await kvp.Value.GetAvailableVersionsAsync(canonicalId, ct);
                if (versions.Count > 0)
                    return versions;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Provider {BackendId} failed for {CanonicalId}", kvp.Key, canonicalId);
            }
        }

        return new List<SemanticVersion>();
    }

    public async Task<List<ModDependency>> GetDependenciesAsync(
        string canonicalId,
        SemanticVersion version,
        CancellationToken ct = default)
    {
        var provider = ResolveProvider(canonicalId);
        if (provider != null)
            return await provider.GetDependenciesAsync(canonicalId, version, ct);

        // No matching provider — try all
        foreach (var kvp in _providers)
        {
            try
            {
                return await kvp.Value.GetDependenciesAsync(canonicalId, version, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Provider {BackendId} failed for {CanonicalId}@{Version}",
                    kvp.Key, canonicalId, version);
            }
        }

        return new List<ModDependency>();
    }

    private IModVersionProvider? ResolveProvider(string canonicalId)
    {
        var colonIndex = canonicalId.IndexOf(':');
        if (colonIndex <= 0)
            return null;

        var prefix = canonicalId[..colonIndex];
        return _providers.TryGetValue(prefix, out var provider) ? provider : null;
    }
}
