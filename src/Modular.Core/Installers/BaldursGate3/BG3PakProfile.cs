using System.Collections.Concurrent;

namespace Modular.Core.Installers.BaldursGate3;

/// <summary>
/// What a pak says about itself: its module metadata and the paths it packs.
/// </summary>
/// <param name="Meta">The pak's <c>meta.lsx</c>, or <c>null</c> for a pak without one.</param>
/// <param name="Files">The virtual paths packed into the pak, with forward slashes.</param>
public sealed record BG3PakProfile(BG3PakMeta? Meta, IReadOnlyList<string> Files)
{
    // A deployment asks about the same pak several times over (once to name it, once to
    // order it, once to check it), and none of those should reopen the file.
    private static readonly ConcurrentDictionary<string, (long Length, DateTime Written, BG3PakProfile Profile)> Cache =
        new(StringComparer.Ordinal);

    /// <summary>Whether any packed path contains the given text, ignoring case.</summary>
    /// <param name="fragment">Text to look for, with forward slashes.</param>
    public bool HasPath(string fragment) =>
        Files.Any(f => f.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether the mod carries Script Extender scripts, and so does nothing without it.
    /// </summary>
    public bool NeedsScriptExtender => HasPath("/ScriptExtender/");

    /// <summary>
    /// Reads a pak's profile.
    /// </summary>
    /// <param name="pakPath">Absolute path to the <c>.pak</c>.</param>
    /// <returns>The profile, or <c>null</c> when the file is missing or is not a readable pak.</returns>
    public static BG3PakProfile? Read(string pakPath)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(pakPath);
            if (!info.Exists)
                return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        if (Cache.TryGetValue(info.FullName, out var cached) &&
            cached.Length == info.Length && cached.Written == info.LastWriteTimeUtc)
        {
            return cached.Profile;
        }

        var files = LspkReader.ReadFileNames(info.FullName);
        if (files.Count == 0)
            return null;

        var profile = new BG3PakProfile(BG3PakMeta.Read(info.FullName), files);
        Cache[info.FullName] = (info.Length, info.LastWriteTimeUtc, profile);
        return profile;
    }
}
