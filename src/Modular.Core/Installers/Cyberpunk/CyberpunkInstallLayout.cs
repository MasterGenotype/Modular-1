namespace Modular.Core.Installers.Cyberpunk;

/// <summary>
/// Describes a detected Cyberpunk 2077 mod layout within an archive.
/// Produced by <see cref="CyberpunkArchiveAnalyzer.Analyze"/> and consumed by
/// <see cref="CyberpunkModInstaller"/> to generate an <see cref="Sdk.Installers.InstallPlan"/>.
/// </summary>
public sealed class CyberpunkInstallLayout
{
    /// <summary>
    /// Bitfield of all installation types detected in the archive.
    /// A multi-path mod will have multiple flags set (e.g. RedscriptMod | LegacyArchive | TweakMod).
    /// </summary>
    public CyberpunkInstallType Types { get; init; }

    /// <summary>
    /// Overall detection confidence (0.0 – 1.0).
    /// Calculated as the highest individual signal confidence, boosted when
    /// multiple non-conflicting types are detected together (common for CP2077 mods).
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// Human-readable reason string for logging and UI.
    /// </summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>
    /// Per-file routing decisions. Key = archive entry FullName, value = relative
    /// destination path under the game root directory.
    /// </summary>
    public Dictionary<string, string> FileRoutes { get; init; } = new();

    /// <summary>
    /// Common root prefix stripped from archive paths (e.g. the mod name folder
    /// that many archive authors wrap their content in).
    /// Empty string when no strippable prefix was found.
    /// </summary>
    public string StrippedPrefix { get; init; } = string.Empty;

    /// <summary>
    /// Detected framework-level files that must be routed to bin/x64, engine/, etc.
    /// </summary>
    public List<FrameworkFileHint> FrameworkHints { get; init; } = new();

    /// <summary>True when a single type flag covers the entire archive.</summary>
    public bool IsSingleType => (Types & (Types - 1)) == 0 && Types != CyberpunkInstallType.Unknown;

    /// <summary>True when the archive contains files targeting multiple distinct directories.</summary>
    public bool IsMultiPath => !IsSingleType && Types != CyberpunkInstallType.Unknown;
}

/// <summary>
/// Hint about a specific framework-level file in the archive.
/// </summary>
public sealed class FrameworkFileHint
{
    /// <summary>Archive entry path.</summary>
    public string SourceEntry { get; init; } = string.Empty;

    /// <summary>Expected destination relative to game root.</summary>
    public string DestinationRelative { get; init; } = string.Empty;

    /// <summary>Which framework this file belongs to.</summary>
    public string FrameworkId { get; init; } = string.Empty;

    /// <summary>Whether this file is the primary loader DLL (e.g. winmm.dll for RED4ext).</summary>
    public bool IsCritical { get; init; }
}
