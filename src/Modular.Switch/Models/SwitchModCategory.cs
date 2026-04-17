namespace Modular.Switch.Models;

/// <summary>
/// Categorises a Switch mod by the LayeredFS target it modifies.
/// These map directly to Yuzu's subfolder conventions inside the load directory.
/// </summary>
public enum SwitchModCategory
{
    /// <summary>Patches the game's read-only file system (atmosphere/exefs/romfs style).</summary>
    RomFs,

    /// <summary>Patches the executable code segments (IPS patches, NSO stubs).</summary>
    ExeFs,

    /// <summary>Cheat codes — Atmosphere/Yuzu *.txt cheat files.</summary>
    Cheats,

    /// <summary>GameBanana-style "content" mods that sit directly under load/<ID>/.</summary>
    Content,

    /// <summary>Unknown or unclassified; discovery will attempt to auto-detect.</summary>
    Unknown
}

public static class SwitchModCategoryExtensions
{
    /// <summary>
    /// Returns the sub-path inside the Yuzu load folder for this category.
    /// e.g. RomFs  → "romfs"
    ///      ExeFs  → "exefs"
    ///      Cheats → "cheats"
    /// Content mods have no extra sub-path (installed directly under the mod folder).
    /// </summary>
    public static string YuzuSubPath(this SwitchModCategory cat) => cat switch
    {
        SwitchModCategory.RomFs   => "romfs",
        SwitchModCategory.ExeFs   => "exefs",
        SwitchModCategory.Cheats  => "cheats",
        SwitchModCategory.Content => "",
        _                         => ""
    };

    /// <summary>Infers the category from a leading path segment found in an archive entry.</summary>
    public static SwitchModCategory FromPathSegment(string segment) =>
        segment.ToLowerInvariant() switch
        {
            "romfs"        => SwitchModCategory.RomFs,
            "exefs"        => SwitchModCategory.ExeFs,
            "cheats"       => SwitchModCategory.Cheats,
            "content"      => SwitchModCategory.Content,
            _              => SwitchModCategory.Unknown
        };
}
