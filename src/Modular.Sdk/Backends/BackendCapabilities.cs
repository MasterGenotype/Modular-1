namespace Modular.Sdk.Backends;

/// <summary>
/// Feature flags declaring what capabilities a backend supports.
/// Used by CLI to conditionally apply features like rate limiting,
/// game domain prompting, file category filtering, etc.
/// </summary>
[Flags]
public enum BackendCapabilities
{
    /// <summary>No special capabilities.</summary>
    None = 0,

    /// <summary>
    /// Backend supports game domain filtering (e.g., NexusMods has skyrimspecialedition, stardewvalley).
    /// If set, CLI should prompt for game domain.
    /// </summary>
    GameDomains = 1 << 0,

    /// <summary>
    /// Backend supports file category filtering (main, optional, update, etc.).
    /// If set, category filters can be applied to file listings.
    /// </summary>
    FileCategories = 1 << 1,

    /// <summary>
    /// Backend provides MD5 hashes for downloaded files.
    /// If set, downloads can be verified against expected checksums.
    /// </summary>
    Md5Verification = 1 << 2,

    /// <summary>
    /// Backend requires rate limiting to avoid API throttling.
    /// If set, rate limiter should be applied before requests.
    /// </summary>
    RateLimited = 1 << 3,

    /// <summary>
    /// Backend requires authentication (API key, login, etc.).
    /// If set, configuration validation should check for credentials.
    /// </summary>
    Authentication = 1 << 4,

    /// <summary>
    /// Backend supports organizing mods into category subdirectories.
    /// If set, mods can be grouped by their category ID.
    /// </summary>
    ModCategories = 1 << 5,
}
