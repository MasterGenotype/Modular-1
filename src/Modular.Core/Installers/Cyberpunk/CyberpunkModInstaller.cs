using Microsoft.Extensions.Logging;
using Modular.Core.Archives;
using Modular.Core.Utilities;
using Modular.Sdk;
using Modular.Sdk.Archives;
using Modular.Sdk.Installers;

namespace Modular.Core.Installers.Cyberpunk;

/// <summary>
/// Installer for Cyberpunk 2077 mods. Uses <see cref="CyberpunkArchiveAnalyzer"/>
/// to fingerprint archive contents and automatically route files to the correct
/// game subdirectories based on detected <see cref="CyberpunkInstallType"/>s.
///
/// Supports all 11 install types identified from the Cyberpunk 2077 modding
/// ecosystem, including multi-path mods that span multiple directories.
///
/// Detection priority is 90 — above generic loose-file (1) and BepInEx (80),
/// but below FOMOD (100) since FOMOD archives carry an explicit manifest.
/// </summary>
public class CyberpunkModInstaller : IModInstaller, IGameAwareInstaller
{
    private readonly IArchiveReaderFactory _archiveReaderFactory;
    private readonly ILogger<CyberpunkModInstaller>? _logger;

    public string InstallerId => "cyberpunk2077";
    public string DisplayName => "Cyberpunk 2077 Installer";
    public int Priority => 90;
    public IReadOnlyList<string> SupportedGameIds { get; } = [GameIds.Cyberpunk2077, "1091500"];

    public CyberpunkModInstaller(
        IArchiveReaderFactory? archiveReaderFactory = null,
        ILogger<CyberpunkModInstaller>? logger = null)
    {
        _archiveReaderFactory = archiveReaderFactory ?? new ArchiveReaderFactory();
        _logger = logger;
    }

    /// <summary>
    /// Opens the archive, runs <see cref="CyberpunkArchiveAnalyzer.Analyze"/>,
    /// and returns a detection result reflecting the confidence and type.
    /// </summary>
    /// <remarks>
    /// With no game to go on, loose layouts are only claimed when the archive also holds
    /// a path unique to Cyberpunk.
    /// </remarks>
    public Task<InstallDetectionResult> DetectAsync(
        string archivePath, CancellationToken ct = default) =>
        DetectAsync(archivePath, gameId: null, ct);

    /// <summary>
    /// Detects the archive's layout, claiming loose layouts — a bare <c>.archive</c>, a
    /// folder with an <c>init.lua</c> — when <paramref name="gameId"/> is Cyberpunk 2077.
    /// </summary>
    /// <param name="archivePath">Archive to analyse.</param>
    /// <param name="gameId">Game identifier, or <c>null</c> when unknown.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The detection result.</returns>
    public async Task<InstallDetectionResult> DetectAsync(
        string archivePath, string? gameId, CancellationToken ct = default)
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

            var layout = CyberpunkArchiveAnalyzer.Analyze(
                reader.Entries, ModNameOf(archivePath), IsCyberpunk(gameId));

            // Claimed, so no lower-priority installer copies the program into the game,
            // and refused when planned, with the reason.
            if (layout.Rejection != null)
            {
                return new InstallDetectionResult
                {
                    CanHandle = true,
                    Confidence = layout.Confidence,
                    InstallerType = "not-a-mod",
                    Reason = layout.Reason
                };
            }

            if (layout.Types == CyberpunkInstallType.Unknown)
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
                "Cyberpunk analyzer: {Types} ({Confidence:P0}) — {Reason}",
                layout.Types, layout.Confidence, layout.Reason);

            return await Task.FromResult(new InstallDetectionResult
            {
                CanHandle = true,
                Confidence = layout.Confidence,
                InstallerType = layout.IsSingleType
                    ? layout.Types.ToString()
                    : "cyberpunk-multipath",
                Reason = layout.Reason
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Cyberpunk detection failed for {Path}", archivePath);
            return new InstallDetectionResult
            {
                CanHandle = false,
                Confidence = 0,
                Reason = $"Error: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Builds an <see cref="InstallPlan"/> using the file routes computed by
    /// <see cref="CyberpunkArchiveAnalyzer"/>. Each file operation maps an
    /// archive entry to its correct game-relative destination path.
    /// </summary>
    public async Task<InstallPlan> AnalyzeAsync(
        string archivePath,
        InstallContext context,
        CancellationToken ct = default)
    {
        using var reader = _archiveReaderFactory.Open(archivePath)
            ?? throw new InvalidOperationException($"Unable to open archive: {archivePath}");

        var layout = CyberpunkArchiveAnalyzer.Analyze(
            reader.Entries,
            ModNameOf(archivePath),
            IsCyberpunk(context.GameId) || IsCyberpunk(context.Game?.GameId));

        if (layout.Rejection != null)
            throw new InvalidOperationException(layout.Rejection);

        var plan = new InstallPlan
        {
            InstallerId = InstallerId,
            SourcePath = archivePath,
            TargetDirectory = context.GameDirectory,
            RootDirectories = context.ModPaths?.Roots,
            Operations = new List<FileOperation>(),
            Options = new Dictionary<string, object>
            {
                ["cyberpunk_types"] = layout.Types.ToString(),
                ["cyberpunk_is_multipath"] = layout.IsMultiPath,
                ["cyberpunk_stripped_prefix"] = layout.StrippedPrefix,
                ["cyberpunk_framework_hints"] = layout.FrameworkHints
                    .Select(h => new Dictionary<string, object>
                    {
                        ["source"] = h.SourceEntry,
                        ["dest"] = h.DestinationRelative,
                        ["framework"] = h.FrameworkId,
                        ["critical"] = h.IsCritical
                    }).ToList()
            }
        };

        long totalBytes = 0;

        foreach (var entry in reader.Entries.Where(e => !e.IsDirectory))
        {
            // Look up the routed destination for this entry, together with the package
            // type and the rule that chose it — recorded on the operation so a mis-route
            // traces back to the rule rather than having to be re-derived.
            if (!layout.FileRoutes.TryGetValue(entry.FullName, out var route))
            {
                // Fallback: place unrecognized files at game root maintaining relative path
                var fallback = entry.FullName;
                if (!string.IsNullOrEmpty(layout.StrippedPrefix) &&
                    fallback.StartsWith(layout.StrippedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    fallback = fallback[layout.StrippedPrefix.Length..];
                }

                route = FileRoute.Declared(fallback, PackageTypes.Unknown);
            }

            // Normalize path separators
            var destination = route.Destination.Replace('\\', '/');

            // Determine criticality
            var isCritical = IsCriticalFile(entry.FullName, layout);

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
            "Cyberpunk install plan: {Count} files, {Types}, {Size:N0} bytes",
            plan.Operations.Count, layout.Types, totalBytes);

        return await Task.FromResult(plan);
    }

    /// <summary>
    /// Executes the install plan, extracting each file to its computed destination.
    /// Critical files (DLLs, loaders) get automatic backups.
    /// </summary>
    public Task<InstallResult> InstallAsync(
        InstallPlan plan,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default) =>
        ArchivePlanExecutor.ExecuteAsync(
            _archiveReaderFactory, plan, InstallerId, BuildProgressLabel(plan), _logger, progress, ct);

    // ── Private helpers ──────────────────────────────────────────────────

    private bool IsCyberpunk(string? gameId) =>
        gameId != null && SupportedGameIds.Contains(gameId, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The name for a mod folder the archive does not name itself: the mod's name from a
    /// NexusMods download name, or else the archive's file name.
    /// </summary>
    private static string ModNameOf(string archivePath) =>
        NexusFileName.ParseModName(archivePath) ?? Path.GetFileNameWithoutExtension(archivePath);

    /// <summary>
    /// A file is critical if it's a framework loader DLL, an ASI plugin,
    /// or marked critical by the analyzer.
    /// </summary>
    private static bool IsCriticalFile(string entryPath, CyberpunkInstallLayout layout)
    {
        // Framework-level files marked by the analyzer
        if (layout.FrameworkHints.Any(h =>
            h.SourceEntry.Equals(entryPath, StringComparison.OrdinalIgnoreCase) && h.IsCritical))
            return true;

        var lower = entryPath.ToLowerInvariant();
        return lower.EndsWith(".dll") || lower.EndsWith(".asi");
    }

    /// <summary>
    /// Build a human-readable progress label based on detected types.
    /// </summary>
    private static string BuildProgressLabel(InstallPlan plan)
    {
        if (plan.Options?.TryGetValue("cyberpunk_types", out var typesObj) == true &&
            typesObj is string types)
        {
            if (types.Contains(','))
                return "Installing Cyberpunk 2077 multi-path mod";

            return types switch
            {
                nameof(CyberpunkInstallType.Red4ExtPlugin) => "Installing RED4ext plugin",
                nameof(CyberpunkInstallType.CetMod) => "Installing CET mod",
                nameof(CyberpunkInstallType.RedscriptMod) => "Installing redscript mod",
                nameof(CyberpunkInstallType.LegacyArchive) => "Installing legacy archive",
                nameof(CyberpunkInstallType.RedMod) => "Installing REDmod",
                nameof(CyberpunkInstallType.TweakMod) => "Installing TweakDB mod",
                nameof(CyberpunkInstallType.IniTweak) => "Installing config tweak",
                nameof(CyberpunkInstallType.FrameworkRoot) => "Installing framework",
                nameof(CyberpunkInstallType.StandaloneExe) => "Extracting standalone tool",
                nameof(CyberpunkInstallType.InputMapping) => "Installing input mappings",
                nameof(CyberpunkInstallType.AsiPlugin) => "Installing ASI plugin",
                _ => "Installing Cyberpunk 2077 mod"
            };
        }

        return "Installing Cyberpunk 2077 mod";
    }
}
