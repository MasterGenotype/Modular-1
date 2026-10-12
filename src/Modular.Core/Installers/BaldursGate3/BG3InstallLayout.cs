using Modular.Sdk.Installers;

namespace Modular.Core.Installers.BaldursGate3;

/// <summary>
/// Describes a detected Baldur's Gate 3 mod layout within an archive.
/// Produced by <see cref="BG3ArchiveAnalyzer.Analyze"/> and consumed by
/// <see cref="BG3ModInstaller"/> to build an <see cref="InstallPlan"/>.
/// </summary>
public sealed class BG3InstallLayout
{
    /// <summary>Whether the archive is recognisable as a BG3 mod at all.</summary>
    public bool IsRecognized => FileRoutes.Count > 0;

    /// <summary>True when the archive carries at least one <c>.pak</c>.</summary>
    public bool HasPak { get; init; }

    /// <summary>True when the archive carries loose files for the install's <c>Data</c> folder.</summary>
    public bool HasLooseData { get; init; }

    /// <summary>Overall detection confidence (0.0 – 1.0).</summary>
    public double Confidence { get; init; }

    /// <summary>Human-readable classification reason for logging and UI.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>
    /// Per-file routing decisions, keyed by archive entry FullName. A pak's route names the
    /// user <c>Mods</c> root; every other route is relative to the game root. Entries with no
    /// route (readmes, mod-manager metadata) are not installed.
    /// </summary>
    public Dictionary<string, FileRoute> FileRoutes { get; init; } = new();

    /// <summary>
    /// Alternatives the archive offers, such as the same pak in an "Option A" and an
    /// "Option B" folder. <see cref="FileRoutes"/> holds only the selected ones.
    /// </summary>
    public IReadOnlyList<InstallChoice> Choices { get; init; } = [];
}
