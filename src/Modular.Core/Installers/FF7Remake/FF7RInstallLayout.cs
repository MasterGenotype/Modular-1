namespace Modular.Core.Installers.FF7Remake;

/// <summary>
/// Describes a detected Final Fantasy VII Remake Intergrade mod layout within
/// an archive. Produced by <see cref="FF7RArchiveAnalyzer.Analyze"/> and
/// consumed by <see cref="FF7RModInstaller"/> to build an
/// <see cref="Sdk.Installers.InstallPlan"/>.
/// </summary>
public sealed class FF7RInstallLayout
{
    /// <summary>
    /// Bitfield of all installation types detected in the archive.
    /// Multi-type mods (e.g. DllHook + IniConfig) carry multiple flags.
    /// </summary>
    public FF7RInstallType Types { get; init; }

    /// <summary>
    /// Overall detection confidence (0.0 – 1.0).
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// Human-readable classification reason for logging and UI.
    /// </summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>
    /// Per-file routing decisions. Key = archive entry FullName, value = relative
    /// destination path under the game root directory.
    /// </summary>
    public Dictionary<string, string> FileRoutes { get; init; } = new();

    /// <summary>
    /// Common root prefix stripped from archive paths.
    /// Empty when no strippable prefix was found.
    /// </summary>
    public string StrippedPrefix { get; init; } = string.Empty;

    /// <summary>
    /// True when the archive contains DLLs that should be placed in
    /// End\Binaries\Win64 — triggers the DX11 requirement warning.
    /// </summary>
    public bool RequiresDx11 { get; init; }

    /// <summary>True when a single type flag covers the entire archive.</summary>
    public bool IsSingleType => (Types & (Types - 1)) == 0 && Types != FF7RInstallType.Unknown;

    /// <summary>True when the archive targets multiple distinct directories.</summary>
    public bool IsMultiPath => !IsSingleType && Types != FF7RInstallType.Unknown;
}
