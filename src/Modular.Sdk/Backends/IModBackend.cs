using Modular.Sdk.Backends.Common;

namespace Modular.Sdk.Backends;

/// <summary>
/// Common interface for all mod repository backends.
/// Each backend (NexusMods, GameBanana, CurseForge, etc.) implements this interface.
/// </summary>
public interface IModBackend
{
    /// <summary>
    /// Unique identifier for this backend (e.g., "nexusmods", "gamebanana").
    /// Used as config keys, CLI subcommand names, and directory names.
    /// Must be lowercase with no spaces.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Human-readable display name (e.g., "NexusMods", "GameBanana").
    /// Used in UI menus and status messages.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Declares what features this backend supports.
    /// Used by CLI to conditionally apply rate limiting, MD5 verification,
    /// category filtering, game domain prompting, etc.
    /// </summary>
    BackendCapabilities Capabilities { get; }

    /// <summary>
    /// Validates that the backend is properly configured (API keys, user IDs, etc.).
    /// </summary>
    /// <returns>
    /// A list of validation error messages, or an empty list if configuration is valid.
    /// </returns>
    IReadOnlyList<string> ValidateConfiguration();

    /// <summary>
    /// Fetches the list of mods the user has tracked/subscribed to.
    /// </summary>
    /// <param name="gameDomain">
    /// Optional game domain to filter by (e.g., "skyrimspecialedition").
    /// Only applicable for backends with GameDomains capability.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of tracked/subscribed mods.</returns>
    Task<List<BackendMod>> GetUserModsAsync(
        string? gameDomain = null,
        CancellationToken ct = default);

    /// <summary>
    /// Fetches downloadable files for a specific mod.
    /// </summary>
    /// <param name="modId">The mod ID (string to support both int and string IDs).</param>
    /// <param name="gameDomain">
    /// Optional game domain (required for some backends like NexusMods).
    /// </param>
    /// <param name="filter">Optional filter for categories, versions, etc.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of downloadable files for the mod.</returns>
    Task<List<BackendModFile>> GetModFilesAsync(
        string modId,
        string? gameDomain = null,
        FileFilter? filter = null,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves a mod file to a direct download URL.
    /// Some backends (NexusMods) require a separate API call to get download URLs.
    /// Others (GameBanana) include URLs directly in file listings.
    /// </summary>
    /// <param name="modId">The mod ID.</param>
    /// <param name="fileId">The file ID to resolve.</param>
    /// <param name="gameDomain">Optional game domain.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The direct download URL, or null if resolution failed.
    /// Returns null for backends that provide URLs inline (check DirectDownloadUrl on BackendModFile).
    /// </returns>
    Task<string?> ResolveDownloadUrlAsync(
        string modId,
        string fileId,
        string? gameDomain = null,
        CancellationToken ct = default);

    /// <summary>
    /// Downloads files for tracked/subscribed mods.
    /// This is the main entry point for bulk download operations.
    /// </summary>
    /// <param name="outputDirectory">Base directory to save downloaded files.</param>
    /// <param name="gameDomain">
    /// Optional game domain (required for backends with GameDomains capability).
    /// </param>
    /// <param name="options">Download options (dry run, force, filters, etc.).</param>
    /// <param name="progress">Progress reporter for UI updates.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DownloadModsAsync(
        string outputDirectory,
        string? gameDomain = null,
        DownloadOptions? options = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Gets mod information by ID.
    /// Useful for fetching mod name, author, etc. for display or renaming.
    /// </summary>
    /// <param name="modId">The mod ID.</param>
    /// <param name="gameDomain">Optional game domain.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Mod information, or null if not found.</returns>
    Task<BackendMod?> GetModInfoAsync(
        string modId,
        string? gameDomain = null,
        CancellationToken ct = default);
}
