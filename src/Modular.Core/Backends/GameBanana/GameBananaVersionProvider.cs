using Microsoft.Extensions.Logging;
using Modular.Core.Dependencies;
using Modular.Core.Metadata;
using Modular.Core.Versioning;
using Modular.Sdk.Backends;
using Modular.Sdk.Backends.Common;

namespace Modular.Core.Backends.GameBanana;

/// <summary>
/// Provides mod version information from GameBanana for dependency resolution.
/// Parses canonical IDs in the format "gamebanana:{modId}".
/// </summary>
public class GameBananaVersionProvider : IModVersionProvider
{
    private readonly IModBackend _backend;
    private readonly ILogger<GameBananaVersionProvider>? _logger;

    public GameBananaVersionProvider(
        IModBackend backend,
        ILogger<GameBananaVersionProvider>? logger = null)
    {
        _backend = backend;
        _logger = logger;
    }

    public async Task<List<SemanticVersion>> GetAvailableVersionsAsync(
        string canonicalId,
        CancellationToken ct = default)
    {
        var modId = ParseCanonicalId(canonicalId);

        var files = await _backend.GetModFilesAsync(modId, ct: ct);

        var versions = new List<SemanticVersion>();
        foreach (var file in files)
        {
            // Try the Version field first, then extract from display/file name
            var versionString = file.Version
                ?? ExtractVersionFromFileName(file.DisplayName ?? file.FileName);

            if (versionString != null && SemanticVersion.TryParse(versionString, out var semver))
            {
                if (!versions.Any(v => v.Equals(semver)))
                {
                    versions.Add(semver!);
                }
            }
        }

        _logger?.LogDebug(
            "Found {Count} parseable versions for {CanonicalId} out of {TotalFiles} files",
            versions.Count, canonicalId, files.Count);

        return versions;
    }

    public Task<List<ModDependency>> GetDependenciesAsync(
        string canonicalId,
        SemanticVersion version,
        CancellationToken ct = default)
    {
        // GameBanana does not expose structured dependency data in its API.
        // Return an empty list.
        _logger?.LogDebug(
            "GameBanana does not provide structured dependency data for {CanonicalId}@{Version}",
            canonicalId, version);

        return Task.FromResult(new List<ModDependency>());
    }

    /// <summary>
    /// Parses a canonical ID to extract the mod ID.
    /// Supported formats:
    ///   "gamebanana:{modId}" - prefixed form
    ///   "{modId}" - bare mod ID
    /// </summary>
    internal static string ParseCanonicalId(string canonicalId)
    {
        var parts = canonicalId.Split(':');

        return parts.Length switch
        {
            2 when parts[0].Equals("gamebanana", StringComparison.OrdinalIgnoreCase)
                => parts[1],

            1 => parts[0],

            _ => throw new ArgumentException(
                $"Invalid GameBanana canonical ID format: '{canonicalId}'. " +
                "Expected 'gamebanana:modId' or bare modId.",
                nameof(canonicalId))
        };
    }

    /// <summary>
    /// Attempts to extract a version string from a file name or display name.
    /// Looks for patterns like "v1.2.3", "1.2.3", "V2.0.0" within the string.
    /// </summary>
    internal static string? ExtractVersionFromFileName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return null;

        // Match version patterns like "v1.2.3", "1.2.3", "v1.0.0-beta.1"
        var match = System.Text.RegularExpressions.Regex.Match(
            fileName,
            @"[vV]?(\d+\.\d+\.\d+(?:-[a-zA-Z0-9.]+)?(?:\+[a-zA-Z0-9.]+)?)");

        if (!match.Success)
            return null;

        // Strip trailing file extension that the greedy regex may have captured
        // (e.g., ".zip" from "2.0.0-beta.1.zip")
        var version = match.Groups[1].Value;
        return System.Text.RegularExpressions.Regex.Replace(
            version, @"\.[a-zA-Z]{2,}$", "");
    }
}
