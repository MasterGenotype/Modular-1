using System.Text.RegularExpressions;

namespace Modular.Core.Utilities;

/// <summary>
/// Reads what NexusMods encodes in the names of the files it serves.
/// </summary>
public static partial class NexusFileName
{
    /// <summary>
    /// Reads the mod id out of a downloaded archive's file name.
    /// </summary>
    /// <param name="archivePath">Path or file name of the archive, with or without extension.</param>
    /// <returns>The mod id, or <c>null</c> when the name is not in NexusMods' form.</returns>
    /// <remarks>
    /// NexusMods names its downloads <c>&lt;name&gt;-&lt;mod id&gt;-&lt;version&gt;-&lt;unix time&gt;</c>.
    /// The name may itself contain <c>-&lt;digits&gt;-</c>, in which case the first such run
    /// is taken as the id; nothing in the file name can tell those apart.
    /// </remarks>
    public static string? ParseModId(string? archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
            return null;

        var name = Path.GetFileName(archivePath);
        foreach (var extension in ArchiveExtensions)
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^extension.Length];
                break;
            }
        }

        var match = Pattern().Match(name);
        if (!match.Success)
            return null;

        var id = match.Groups["id"].Value.TrimStart('0');
        return id.Length > 0 ? id : null;
    }

    // Stripped by hand rather than with Path: a mod name ending "2.0" would otherwise
    // lose its ".0" when there is no extension to remove.
    private static readonly string[] ArchiveExtensions =
        [".tar.gz", ".zip", ".rar", ".7z", ".tar", ".pak"];

    [GeneratedRegex(@"^.+?-(?<id>\d+)-.+-\d{9,11}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
