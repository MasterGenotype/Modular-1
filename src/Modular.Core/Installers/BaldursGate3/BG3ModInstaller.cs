using Microsoft.Extensions.Logging;
using Modular.Core.Archives;
using Modular.Sdk;
using Modular.Sdk.Archives;
using Modular.Sdk.Installers;

namespace Modular.Core.Installers.BaldursGate3;

/// <summary>
/// Installer for Baldur's Gate 3 mods. Uses <see cref="BG3ArchiveAnalyzer"/> to split an
/// archive between the game's two mod locations: <c>.pak</c> files go to the user
/// <c>Mods</c> folder, loose files go beneath <c>&lt;install&gt;/Data/</c>.
///
/// Priority 91 — on par with the other game-specific installers; it only competes for
/// archives of its own game.
/// </summary>
public class BG3ModInstaller : IModInstaller
{
    private readonly IArchiveReaderFactory _archiveReaderFactory;
    private readonly ILogger<BG3ModInstaller>? _logger;

    public string InstallerId => "baldurs-gate-3";
    public string DisplayName => "Baldur's Gate 3 Installer";
    public int Priority => 91;
    public IReadOnlyList<string> SupportedGameIds { get; } = [GameIds.BaldursGate3, "1086940"];

    public BG3ModInstaller(
        IArchiveReaderFactory? archiveReaderFactory = null,
        ILogger<BG3ModInstaller>? logger = null)
    {
        _archiveReaderFactory = archiveReaderFactory ?? new ArchiveReaderFactory();
        _logger = logger;
    }

    public Task<InstallDetectionResult> DetectAsync(
        string archivePath, CancellationToken ct = default)
    {
        try
        {
            using var reader = _archiveReaderFactory.Open(archivePath);
            if (reader == null)
            {
                return Task.FromResult(new InstallDetectionResult
                {
                    CanHandle = false,
                    Confidence = 0,
                    Reason = "Unable to open archive"
                });
            }

            var layout = BG3ArchiveAnalyzer.Analyze(reader.Entries);

            _logger?.LogDebug(
                "BG3 analyzer: {Confidence:P0} — {Reason}", layout.Confidence, layout.Reason);

            return Task.FromResult(new InstallDetectionResult
            {
                CanHandle = layout.IsRecognized,
                Confidence = layout.Confidence,
                InstallerType = !layout.IsRecognized
                    ? "unknown"
                    : layout.HasPak && layout.HasLooseData
                        ? "bg3-multipath"
                        : layout.HasPak ? "bg3-pak" : "bg3-loose",
                Reason = layout.Reason
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "BG3 detection failed for {Path}", archivePath);
            return Task.FromResult(new InstallDetectionResult
            {
                CanHandle = false,
                Confidence = 0,
                Reason = $"Error: {ex.Message}"
            });
        }
    }

    public Task<InstallPlan> AnalyzeAsync(
        string archivePath,
        InstallContext context,
        CancellationToken ct = default)
    {
        using var reader = _archiveReaderFactory.Open(archivePath)
            ?? throw new InvalidOperationException($"Unable to open archive: {archivePath}");

        var layout = BG3ArchiveAnalyzer.Analyze(reader.Entries, context.Selections);

        var plan = new InstallPlan
        {
            InstallerId = InstallerId,
            SourcePath = archivePath,
            // Game-root routes are fully qualified from the game root; pak routes name
            // their own root.
            TargetDirectory = context.GameDirectory,
            RootDirectories = context.ModPaths?.Roots,
            Operations = new List<FileOperation>(),
            Choices = layout.Choices.ToList(),
            RequiresUserInput = layout.Choices.Any(c => !c.IsResolved),
            Options = new Dictionary<string, object>
            {
                ["bg3_has_pak"] = layout.HasPak,
                ["bg3_has_loose_data"] = layout.HasLooseData
            }
        };

        long totalBytes = 0;

        foreach (var entry in reader.Entries.Where(e => !e.IsDirectory))
        {
            // No route means documentation or mod-manager metadata, which the game
            // has no use for.
            if (!layout.FileRoutes.TryGetValue(entry.FullName, out var route))
                continue;

            plan.Operations.Add(new FileOperation
            {
                Type = FileOperationType.Extract,
                SourcePath = entry.FullName,
                DestinationPath = route.Destination,
                SizeBytes = entry.Length,
                IsCritical = route.PackageType is PackageTypes.Bg3Native or PackageTypes.Bg3ScriptExtender,
                TargetRoot = route.Root,
                PackageType = route.PackageType,
                RouteSource = route.RouteSource
            });

            totalBytes += entry.Length;
        }

        plan.TotalBytes = totalBytes;

        _logger?.LogInformation(
            "BG3 install plan: {Count} files, {Size:N0} bytes — {Reason}",
            plan.Operations.Count, totalBytes, layout.Reason);

        return Task.FromResult(plan);
    }

    public Task<InstallResult> InstallAsync(
        InstallPlan plan,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default) =>
        ArchivePlanExecutor.ExecuteAsync(
            _archiveReaderFactory, plan, InstallerId, "Installing Baldur's Gate 3 mod", _logger, progress, ct);
}
