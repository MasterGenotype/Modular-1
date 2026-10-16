using Microsoft.Extensions.Logging;
using Modular.Core.Backends.GameBanana;
using Modular.Core.Dependencies;
using Modular.Core.Metadata;
using Modular.Core.Versioning;
using Modular.Sdk.Backends;

namespace Modular.Core.Backends.ModIo;

/// <summary>
/// Provides mod version information from mod.io for dependency resolution.
/// Parses canonical IDs in the format "modio:{modId}" or "modio:{gameId}:{modId}".
/// </summary>
public class ModIoVersionProvider : IModVersionProvider
{
    private readonly IModBackend _backend;
    private readonly ILogger<ModIoVersionProvider>? _logger;

    public ModIoVersionProvider(IModBackend backend, ILogger<ModIoVersionProvider>? logger = null)
    {
        _backend = backend;
        _logger = logger;
    }

    public async Task<List<SemanticVersion>> GetAvailableVersionsAsync(
        string canonicalId,
        CancellationToken ct = default)
    {
        var (gameId, modId) = ParseCanonicalId(canonicalId);

        // Every upload, not just the live file: an older version may be the one required.
        var files = await _backend.GetModFilesAsync(modId, gameId, ct: ct);

        var versions = new List<SemanticVersion>();
        foreach (var file in files)
        {
            var versionString = file.Version
                ?? GameBananaVersionProvider.ExtractVersionFromFileName(file.FileName);

            if (versionString != null &&
                SemanticVersion.TryParse(versionString, out var semver) &&
                !versions.Any(v => v.Equals(semver)))
            {
                versions.Add(semver!);
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
        // mod.io dependencies belong to the mod, not to a version of it, and carry no
        // version range; the requirements check reads them instead.
        return Task.FromResult(new List<ModDependency>());
    }

    /// <summary>
    /// Parses a canonical ID into its game id (when present) and mod id.
    /// Supported formats: "modio:{gameId}:{modId}", "modio:{modId}" and a bare "{modId}".
    /// </summary>
    internal static (string? GameId, string ModId) ParseCanonicalId(string canonicalId)
    {
        var parts = canonicalId.Split(':');
        var prefixed = parts.Length > 1 &&
                       parts[0].Equals(BackendIds.ModIo, StringComparison.OrdinalIgnoreCase);

        return (parts.Length, prefixed) switch
        {
            (3, true) => (parts[1], parts[2]),
            (2, true) => (null, parts[1]),
            (1, _) => (null, parts[0]),
            _ => throw new ArgumentException(
                $"Invalid mod.io canonical ID format: '{canonicalId}'. " +
                "Expected 'modio:gameId:modId', 'modio:modId' or a bare modId.",
                nameof(canonicalId))
        };
    }
}
