using System.Globalization;
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
    /// is taken as the id; nothing in the file name can tell those apart. Newer downloads
    /// are named <c>&lt;name&gt; &lt;mod id&gt; &lt;version&gt; &lt;yyyy-MM-ddTHH-mmZ&gt; &lt;code&gt;</c>.
    /// </remarks>
    public static string? ParseModId(string? archivePath)
    {
        var match = Match(archivePath);
        if (match == null)
            return null;

        var id = match.Groups["id"].Value.TrimStart('0');
        return id.Length > 0 ? id : null;
    }

    /// <summary>
    /// Reads the mod's name out of a downloaded archive's file name.
    /// </summary>
    /// <param name="archivePath">Path or file name of the archive, with or without extension.</param>
    /// <returns>The name, or <c>null</c> when the file name is not in NexusMods' form.</returns>
    /// <remarks>
    /// NexusMods replaces spaces with underscores in some downloads; the name is returned
    /// as it appears.
    /// </remarks>
    public static string? ParseModName(string? archivePath) =>
        Match(archivePath)?.Groups["name"].Value;

    /// <summary>
    /// Reads when the file was uploaded out of a downloaded archive's file name.
    /// </summary>
    /// <param name="archivePath">Path or file name of the archive, with or without extension.</param>
    /// <returns>The upload time, or <c>null</c> when the file name is not in NexusMods' form.</returns>
    public static DateTimeOffset? ParseUploadTime(string? archivePath)
    {
        if (Match(archivePath) is not { } match)
            return null;

        if (long.TryParse(match.Groups["time"].Value, out var seconds))
            return DateTimeOffset.FromUnixTimeSeconds(seconds);

        return DateTimeOffset.TryParseExact(
            match.Groups["stamp"].Value, "yyyy-MM-dd'T'HH-mm'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var stamp)
            ? stamp
            : null;
    }

    private static Match? Match(string? archivePath)
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
            match = SpacedPattern().Match(name);
        return match.Success ? match : null;
    }

    // Stripped by hand rather than with Path: a mod name ending "2.0" would otherwise
    // lose its ".0" when there is no extension to remove.
    private static readonly string[] ArchiveExtensions =
        [".tar.gz", ".zip", ".rar", ".7z", ".tar", ".pak"];

    [GeneratedRegex(@"^(?<name>.+?)-(?<id>\d+)-.+-(?<time>\d{9,11})$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    // "KAVT - Kazstra's Virtual Tav 16325 2.0 2026-10-31T23-05Z rxn6oH0JD".
    [GeneratedRegex(
        @"^(?<name>.+?) (?<id>\d+) \S+ (?<stamp>\d{4}-\d{2}-\d{2}T\d{2}-\d{2}Z) [A-Za-z0-9]+$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SpacedPattern();
}
