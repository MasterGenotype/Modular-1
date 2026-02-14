namespace Modular.Core.Metadata;

/// <summary>
/// Interface for enrichers that transform backend-native metadata to canonical format.
/// Each backend implements this to map its API responses to CanonicalMod.
/// </summary>
public interface IMetadataEnricher
{
    /// <summary>
    /// The backend ID this enricher supports (e.g., "nexusmods", "gamebanana").
    /// </summary>
    string BackendId { get; }

    /// <summary>
    /// Enriches basic mod information into canonical format.
    /// Fetches additional metadata if needed.
    /// </summary>
    /// <param name="modId">Backend-specific mod ID</param>
    /// <param name="gameDomain">Optional game domain/identifier</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Canonical mod with enriched metadata, or null if fetch fails</returns>
    Task<CanonicalMod?> EnrichModAsync(
        string modId,
        string? gameDomain = null,
        CancellationToken ct = default);

    /// <summary>
    /// Enriches a batch of mods efficiently (with rate limiting and caching).
    /// </summary>
    /// <param name="modIds">List of mod IDs to enrich</param>
    /// <param name="gameDomain">Optional game domain/identifier</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of canonical mods (may be partial if some fetches fail)</returns>
    Task<List<CanonicalMod>> EnrichModsBatchAsync(
        IEnumerable<string> modIds,
        string? gameDomain = null,
        CancellationToken ct = default);
}
