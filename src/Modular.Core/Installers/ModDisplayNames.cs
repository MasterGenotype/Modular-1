using System.Text.RegularExpressions;
using Modular.Core.Database;

namespace Modular.Core.Installers;

/// <summary>
/// Picks the name an installed mod is listed under. A Nexus mod installed by its number
/// (<c>--mod-id 3620</c>) must not be listed as "3620".
/// </summary>
public static partial class ModDisplayNames
{
    /// <summary>True when the id is a bare number (a Nexus or GameBanana mod id), not a name.</summary>
    public static bool IsNumericId(string? id) => !string.IsNullOrEmpty(id) && id.All(char.IsAsciiDigit);

    /// <summary>
    /// The best name available without the network: the metadata cache entry for the mod in any of
    /// the game's Nexus domains, else the name part of the archive's file name, else null.
    /// </summary>
    public static string? Resolve(string modId, IEnumerable<string> nexusDomains, string? archivePath, ModMetadataCache? cache)
    {
        if (cache is not null && int.TryParse(modId, out var numeric))
        {
            foreach (var domain in nexusDomains)
            {
                if (cache.GetModMetadata(domain, numeric) is { Name.Length: > 0 } meta)
                    return meta.Name;
            }
        }
        return archivePath is null ? null : FromArchiveFileName(archivePath, modId);
    }

    /// <summary>
    /// The mod name in an archive file name: Nexus names files <c>Name-&lt;modId&gt;-&lt;version&gt;-&lt;timestamp&gt;</c>;
    /// Nexus' newer downloads use <c>Name &lt;modId&gt; &lt;version&gt; …</c>. Null when neither fits.
    /// </summary>
    public static string? FromArchiveFileName(string archivePath, string? modId)
    {
        var stem = Path.GetFileNameWithoutExtension(archivePath);
        if (stem.EndsWith(".modular", StringComparison.OrdinalIgnoreCase))
            stem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(stem));
        if (string.IsNullOrWhiteSpace(stem))
            return null;
        if (IsNumericId(modId))
        {
            foreach (var separator in new[] { $"-{modId}-", $" {modId} " })
            {
                var at = stem.IndexOf(separator, StringComparison.Ordinal);
                if (at > 0)
                    return Tidy(stem[..at]);
            }
        }
        var m = NexusFileName().Match(stem);
        return m.Success ? Tidy(m.Groups["name"].Value) : null;
    }

    // Drops the leading "<modId> <fileId> " some Nexus downloads carry, and underscores.
    private static string Tidy(string name) =>
        LeadingNumbers().Replace(name.Replace('_', ' '), "").Trim(' ', '-');

    [GeneratedRegex(@"^(\d+\s+)+")]
    private static partial Regex LeadingNumbers();

    [GeneratedRegex(@"^(?<name>.+?)-\d+(-[\w.]+)*-\d{9,11}$")]
    private static partial Regex NexusFileName();
}
