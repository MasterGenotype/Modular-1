using Microsoft.Extensions.Logging;
using Modular.Core.Archives;
using Modular.Core.Utilities;
using Modular.Sdk;
using Modular.Sdk.Archives;
using Modular.Sdk.Installers;

namespace Modular.Core.Installers.FF7Remake;

/// <summary>
/// Installer for Final Fantasy VII Remake Intergrade mods. Uses
/// <see cref="FF7RArchiveAnalyzer"/> to fingerprint archive contents and
/// automatically route files to the correct game subdirectories based on
/// detected <see cref="FF7RInstallType"/>s.
///
/// Supports 7 install types: pak mods (~mods or Paks root), DLL hooks,
/// 3DMigoto framework + sub-mods, DXVK wrappers, and INI config files.
///
/// Priority 92 — above the generic UnrealPakInstaller (90). When the
/// analyzer detects FF7R-specific structure, this installer wins; archives
/// that only contain bare .pak files without any FF7R anchor fall through
/// to the generic UE4 handler.
/// </summary>
public class FF7RModInstaller : IModInstaller
{
    private readonly IArchiveReaderFactory _archiveReaderFactory;
    private readonly ILogger<FF7RModInstaller>? _logger;

    public string InstallerId => "ff7remake";
    public string DisplayName => "Final Fantasy VII Remake Installer";
    public int Priority => 92;
    public IReadOnlyList<string> SupportedGameIds { get; } = [GameIds.FinalFantasy7Remake, "1462040"];

    /// <summary>
    /// The UE4 paks sub-path specific to FF7R Intergrade.
    /// </summary>
    private const string PaksSubPath = "End/Content/Paks";
    private const string BinariesSubPath = "End/Binaries/Win64";

    public FF7RModInstaller(
        IArchiveReaderFactory? archiveReaderFactory = null,
        ILogger<FF7RModInstaller>? logger = null)
    {
        _archiveReaderFactory = archiveReaderFactory ?? new ArchiveReaderFactory();
        _logger = logger;
    }

    public async Task<InstallDetectionResult> DetectAsync(
        string archivePath, CancellationToken ct = default)
    {
        try
        {
            using var reader = _archiveReaderFactory.Open(archivePath);
            if (reader == null)
            {
                return new InstallDetectionResult
                {
                    CanHandle = false,
                    Confidence = 0,
                    Reason = "Unable to open archive"
                };
            }

            var layout = FF7RArchiveAnalyzer.Analyze(reader.Entries);

            if (layout.Types == FF7RInstallType.Unknown)
            {
                return await Task.FromResult(new InstallDetectionResult
                {
                    CanHandle = false,
                    Confidence = 0,
                    InstallerType = "unknown",
                    Reason = layout.Reason
                });
            }

            _logger?.LogDebug(
                "FF7R analyzer: {Types} ({Confidence:P0}) — {Reason}",
                layout.Types, layout.Confidence, layout.Reason);

            return await Task.FromResult(new InstallDetectionResult
            {
                CanHandle = true,
                Confidence = layout.Confidence,
                InstallerType = layout.IsSingleType
                    ? layout.Types.ToString()
                    : "ff7r-multipath",
                Reason = layout.Reason
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "FF7R detection failed for {Path}", archivePath);
            return new InstallDetectionResult
            {
                CanHandle = false,
                Confidence = 0,
                Reason = $"Error: {ex.Message}"
            };
        }
    }

    public async Task<InstallPlan> AnalyzeAsync(
        string archivePath,
        InstallContext context,
        CancellationToken ct = default)
    {
        using var reader = _archiveReaderFactory.Open(archivePath)
            ?? throw new InvalidOperationException($"Unable to open archive: {archivePath}");

        var layout = FF7RArchiveAnalyzer.Analyze(reader.Entries);

        // The plan always targets the game root. The analyzer's routes are
        // game-root-relative, including pak routes, so the archive's shape no longer
        // decides where its paks land.
        var targetDir = context.GameDirectory;

        var plan = new InstallPlan
        {
            InstallerId = InstallerId,
            SourcePath = archivePath,
            TargetDirectory = targetDir,
            RootDirectories = context.ModPaths?.Roots,
            Operations = new List<FileOperation>(),
            Options = new Dictionary<string, object>
            {
                ["ff7r_types"] = layout.Types.ToString(),
                ["ff7r_is_multipath"] = layout.IsMultiPath,
                ["ff7r_requires_dx11"] = layout.RequiresDx11,
                ["ff7r_stripped_prefix"] = layout.StrippedPrefix
            }
        };

        long totalBytes = 0;

        foreach (var entry in reader.Entries.Where(e => !e.IsDirectory))
        {
            if (!layout.FileRoutes.TryGetValue(entry.FullName, out var route))
            {
                var fallback = entry.FullName;
                if (!string.IsNullOrEmpty(layout.StrippedPrefix) &&
                    fallback.StartsWith(layout.StrippedPrefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    fallback = fallback[layout.StrippedPrefix.Length..];
                }

                route = FileRoute.Declared(fallback, PackageTypes.Unknown);
            }

            var destination = route.Destination.Replace('\\', '/');

            // Every game-rooted destination is relative to the game root; a route that
            // names its own root (a user config INI) is relative to that root instead.
            var isCritical =
                destination.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                destination.EndsWith(".asi", StringComparison.OrdinalIgnoreCase) ||
                destination.EndsWith(".pak", StringComparison.OrdinalIgnoreCase);

            plan.Operations.Add(new FileOperation
            {
                Type = FileOperationType.Extract,
                SourcePath = entry.FullName,
                DestinationPath = destination,
                SizeBytes = entry.Length,
                IsCritical = isCritical,
                TargetRoot = route.Root,
                PackageType = route.PackageType,
                RouteSource = route.RouteSource
            });

            totalBytes += entry.Length;
        }

        plan.TotalBytes = totalBytes;

        _logger?.LogInformation(
            "FF7R install plan: {Count} files, {Types}, {Size:N0} bytes, DX11={Dx11}",
            plan.Operations.Count, layout.Types, totalBytes, layout.RequiresDx11);

        return await Task.FromResult(plan);
    }

    public Task<InstallResult> InstallAsync(
        InstallPlan plan,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default) =>
        ArchivePlanExecutor.ExecuteAsync(
            _archiveReaderFactory, plan, InstallerId, BuildProgressLabel(plan), _logger, progress, ct);

    // ── Private helpers ──────────────────────────────────────────────────

    private static string BuildProgressLabel(InstallPlan plan)
    {
        if (plan.Options?.TryGetValue("ff7r_types", out var typesObj) == true &&
            typesObj is string types)
        {
            if (types.Contains(','))
                return "Installing FF7R multi-path mod";

            return types switch
            {
                nameof(FF7RInstallType.PakMod) => "Installing FF7R pak mod",
                nameof(FF7RInstallType.PakRoot) => "Installing FF7R pak (root)",
                nameof(FF7RInstallType.DllHook) => "Installing FF7R DLL hook",
                nameof(FF7RInstallType.ThreeDMigoto) => "Installing 3DMigoto framework",
                nameof(FF7RInstallType.ThreeDMigotoMod) => "Installing 3DMigoto mod",
                nameof(FF7RInstallType.DxvkWrapper) => "Installing DXVK wrapper",
                nameof(FF7RInstallType.IniConfig) => "Installing config files",
                _ => "Installing FF7 Remake mod"
            };
        }

        return "Installing FF7 Remake mod";
    }
}
