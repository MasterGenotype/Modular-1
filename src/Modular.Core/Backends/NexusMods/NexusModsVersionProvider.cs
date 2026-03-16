using Microsoft.Extensions.Logging;
using Modular.Core.Dependencies;
using Modular.Core.Metadata;
using Modular.Core.Versioning;
using Modular.Sdk.Backends;
using Modular.Sdk.Backends.Common;

namespace Modular.Core.Backends.NexusMods;

/// <summary>
/// Provides mod version information from NexusMods for dependency resolution.
/// Parses canonical IDs in the format "nexusmods:{modId}" or "nexusmods:{gameDomain}:{modId}".
/// </summary>
public class NexusModsVersionProvider : IModVersionProvider
{
    private readonly IModBackend _backend;
    private readonly string _defaultGameDomain;
    private readonly ILogger<NexusModsVersionProvider>? _logger;

    public NexusModsVersionProvider(
        IModBackend backend,
        string defaultGameDomain,
        ILogger<NexusModsVersionProvider>? logger = null)
    {
        _backend = backend;
        _defaultGameDomain = defaultGameDomain;
        _logger = logger;
    }

    public async Task<List<SemanticVersion>> GetAvailableVersionsAsync(
        string canonicalId,
        CancellationToken ct = default)
    {
        var (gameDomain, modId) = ParseCanonicalId(canonicalId);

        var files = await _backend.GetModFilesAsync(modId, gameDomain, ct: ct);

        return ExtractVersions(files);
    }

    public Task<List<ModDependency>> GetDependenciesAsync(
        string canonicalId,
        SemanticVersion version,
        CancellationToken ct = default)
    {
        // NexusMods API does not expose structured dependency data.
        // Return an empty list; dependencies would need to come from
        // FOMOD metadata or manual user configuration.
        _logger?.LogDebug(
            "NexusMods does not provide structured dependency data for {CanonicalId}@{Version}",
            canonicalId, version);

        return Task.FromResult(new List<ModDependency>());
    }

    /// <summary>
    /// Extracts unique semantic versions from a list of backend mod files.
    /// </summary>
    internal static List<SemanticVersion> ExtractVersions(List<BackendModFile> files)
    {
        var versions = new List<SemanticVersion>();
        foreach (var file in files)
        {
            if (!string.IsNullOrEmpty(file.Version) &&
                SemanticVersion.TryParse(file.Version, out var semver))
            {
                if (!versions.Any(v => v.Equals(semver)))
                {
                    versions.Add(semver!);
                }
            }
        }
        return versions;
    }

    /// <summary>
    /// Parses a canonical ID into game domain and mod ID.
    /// Supported formats:
    ///   "nexusmods:{modId}" - uses default game domain
    ///   "nexusmods:{gameDomain}:{modId}" - explicit game domain
    ///   "{modId}" - bare mod ID, uses default game domain
    /// </summary>
    internal (string gameDomain, string modId) ParseCanonicalId(string canonicalId)
    {
        var parts = canonicalId.Split(':');

        return parts.Length switch
        {
            // "nexusmods:gameDomain:modId"
            3 when parts[0].Equals("nexusmods", StringComparison.OrdinalIgnoreCase)
                => (parts[1], parts[2]),

            // "nexusmods:modId"
            2 when parts[0].Equals("nexusmods", StringComparison.OrdinalIgnoreCase)
                => (_defaultGameDomain, parts[1]),

            // bare modId
            1 => (_defaultGameDomain, parts[0]),

            _ => throw new ArgumentException(
                $"Invalid NexusMods canonical ID format: '{canonicalId}'. " +
                "Expected 'nexusmods:modId', 'nexusmods:gameDomain:modId', or bare modId.",
                nameof(canonicalId))
        };
    }
}
