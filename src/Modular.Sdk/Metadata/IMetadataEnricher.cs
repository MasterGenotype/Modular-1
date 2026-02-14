namespace Modular.Sdk.Metadata;

/// <summary>
/// Interface for enriching backend-specific metadata into canonical format.
/// Plugins can implement this to add support for new mod backends.
/// </summary>
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
