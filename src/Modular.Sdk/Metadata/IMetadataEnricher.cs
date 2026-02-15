namespace Modular.Sdk.Metadata;

/// <summary>
/// Generic plugin interface for enriching backend-specific metadata.
/// Plugins can implement this to add support for new mod backends.
/// </summary>
/// <remarks>
/// This interface uses generic <c>object</c> types for plugin flexibility.
/// For the strongly-typed interface used by built-in backends, see
/// <c>Modular.Core.Metadata.IMetadataEnricher</c> which returns <c>CanonicalMod</c>.
/// </remarks>
public interface IMetadataEnricher
{
    /// <summary>
    /// Backend identifier (e.g. "nexusmods", "gamebanana").
    /// </summary>
    string BackendId { get; }

    /// <summary>
    /// Enriches backend-specific metadata into canonical format.
    /// </summary>
    /// <param name="backendMetadata">Backend-specific metadata object.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Canonical mod representation.</returns>
    Task<object> EnrichAsync(object backendMetadata, CancellationToken ct = default);
}
