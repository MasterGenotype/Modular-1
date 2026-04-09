namespace Modular.Core.Installers.HorizonZeroDawn;

/// <summary>
/// Describes a detected Horizon Zero Dawn mod layout within an archive.
/// Produced by <see cref="HZDArchiveAnalyzer.Analyze"/> and consumed by
/// <see cref="HZDModInstaller"/> to build an
/// <see cref="Sdk.Installers.InstallPlan"/>.
/// </summary>
public sealed class HZDInstallLayout
{
    /// <summary>
    /// Bitfield of all installation types detected in the archive.
    /// </summary>
    public HZDInstallType Types { get; init; }

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
    /// All routes are fully qualified from game root (e.g. "Packed_DX12/Patch_mod.bin").
    /// </summary>
    public Dictionary<string, string> FileRoutes { get; init; } = new();

    /// <summary>
    /// Common root prefix stripped from archive paths.
    /// </summary>
    public string StrippedPrefix { get; init; } = string.Empty;

    /// <summary>True when a single type flag covers the entire archive.</summary>
    public bool IsSingleType => (Types & (Types - 1)) == 0 && Types != HZDInstallType.Unknown;

    /// <summary>True when the archive targets multiple distinct directories.</summary>
    public bool IsMultiPath => !IsSingleType && Types != HZDInstallType.Unknown;
}
