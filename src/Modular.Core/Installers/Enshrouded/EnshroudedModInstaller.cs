using Microsoft.Extensions.Logging;
using Modular.Core.Archives;
using Modular.Sdk;
using Modular.Sdk.Archives;
using Modular.Sdk.Installers;

namespace Modular.Core.Installers.Enshrouded;

/// <summary>
/// Installs Enshrouded mods: EML mod folders into <c>mods/&lt;id&gt;/</c>, and loader DLLs
/// beside <c>enshrouded.exe</c>. See <see cref="EnshroudedArchiveAnalyzer"/>.
/// </summary>
/// <remarks>
/// Priority 91, like the other game-specific installers: above BepInEx (80) and the
/// loose-file fallback, below FOMOD (100).
/// </remarks>
public sealed class EnshroudedModInstaller : IModInstaller
{
    /// <summary>Largest manifest read, so a mislabelled entry cannot exhaust memory.</summary>
    private const long MaxManifestBytes = 1024 * 1024;

    private readonly IArchiveReaderFactory _archiveReaderFactory;
    private readonly ILogger<EnshroudedModInstaller>? _logger;

    /// <summary>
    /// Creates the installer.
    /// </summary>
    /// <param name="archiveReaderFactory">Shared archive reader factory.</param>
    /// <param name="logger">Optional logger.</param>
    public EnshroudedModInstaller(
        IArchiveReaderFactory? archiveReaderFactory = null,
        ILogger<EnshroudedModInstaller>? logger = null)
    {
        _archiveReaderFactory = archiveReaderFactory ?? new ArchiveReaderFactory();
        _logger = logger;
    }

    /// <inheritdoc />
    public string InstallerId => "enshrouded";

    /// <inheritdoc />
    public string DisplayName => "Enshrouded Installer";

    /// <inheritdoc />
    public int Priority => 91;

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedGameIds { get; } = [GameIds.Enshrouded, "1203620"];

    /// <inheritdoc />
    public Task<InstallDetectionResult> DetectAsync(string archivePath, CancellationToken ct = default)
    {
        try
        {
            using var reader = _archiveReaderFactory.Open(archivePath);
            if (reader == null)
            {
                return Task.FromResult(new InstallDetectionResult
                {
                    CanHandle = false,
                    Reason = "Unable to open archive"
                });
            }

            var layout = Analyze(reader, archivePath);

            return Task.FromResult(new InstallDetectionResult
            {
                CanHandle = layout.CanHandle,
                Confidence = layout.CanHandle ? layout.Confidence : 0,
                InstallerType = InstallerType(layout),
                Reason = layout.Reason
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Enshrouded detection failed for {Path}", archivePath);
            return Task.FromResult(new InstallDetectionResult
            {
                CanHandle = false,
                Reason = $"Error: {ex.Message}"
            });
        }
    }

    /// <inheritdoc />
    public Task<InstallPlan> AnalyzeAsync(
        string archivePath,
        InstallContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        using var reader = _archiveReaderFactory.Open(archivePath)
            ?? throw new InvalidOperationException($"Unable to open archive: {archivePath}");

        var layout = Analyze(reader, archivePath, context.GameDirectory);

        if (!layout.CanHandle)
            throw new InvalidOperationException(layout.Reason);

        var plan = new InstallPlan
        {
            InstallerId = InstallerId,
            SourcePath = archivePath,
            TargetDirectory = context.GameDirectory,
            RootDirectories = context.ModPaths?.Roots,
            Operations = new List<FileOperation>(),
            Options = new Dictionary<string, object>
            {
                ["enshrouded_mods"] = string.Join(",", layout.Mods.Select(m => m.Folder)),
                ["enshrouded_loader"] = layout.LoaderFiles.Count > 0
            }
        };

        if (layout.SkippedFiles.Count > 0)
        {
            plan.Options["enshrouded_skipped"] = string.Join(", ", layout.SkippedFiles);
            _logger?.LogInformation(
                "Not installed (outside any mod folder): {Files}", plan.Options["enshrouded_skipped"]);
        }

        long totalBytes = 0;

        foreach (var entry in reader.Entries.Where(e => !e.IsDirectory))
        {
            if (!layout.FileRoutes.TryGetValue(entry.FullName, out var route))
                continue;

            plan.Operations.Add(new FileOperation
            {
                Type = FileOperationType.Extract,
                SourcePath = entry.FullName,
                DestinationPath = route.Destination,
                SizeBytes = entry.Length,
                IsCritical = route.PackageType == PackageTypes.EnshroudedLoader ||
                             route.Destination.EndsWith("/" + EnshroudedArchiveAnalyzer.ManifestName, StringComparison.OrdinalIgnoreCase),
                TargetRoot = route.Root,
                PackageType = route.PackageType,
                RouteSource = route.RouteSource
            });

            totalBytes += entry.Length;
        }

        plan.TotalBytes = totalBytes;

        _logger?.LogInformation(
            "Enshrouded install plan: {Count} files — {Reason}", plan.Operations.Count, layout.Reason);

        return Task.FromResult(plan);
    }

    /// <inheritdoc />
    public Task<InstallResult> InstallAsync(
        InstallPlan plan,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default) =>
        ArchivePlanExecutor.ExecuteAsync(
            _archiveReaderFactory, plan, InstallerId, ProgressLabel(plan), _logger, progress, ct);

    private static EnshroudedInstallLayout Analyze(
        IArchiveReader reader, string archivePath, string? gameDirectory = null) =>
        EnshroudedArchiveAnalyzer.Analyze(
            reader.Entries,
            entry => ReadText(reader, entry),
            Path.GetFileName(archivePath),
            gameDirectory);

    private static string? ReadText(IArchiveReader reader, ArchiveEntry entry)
    {
        if (entry.Length > MaxManifestBytes)
            return null;

        try
        {
            using var stream = reader.OpenEntryStream(entry);
            using var text = new StreamReader(stream);
            return text.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    private static string InstallerType(EnshroudedInstallLayout layout) =>
        layout.Mods.Count > 0 && layout.LoaderFiles.Count > 0 ? "enshrouded-mod+loader"
        : layout.LoaderFiles.Count > 0 ? "enshrouded-loader"
        : layout.Mods.Count > 0 ? "enshrouded-mod"
        : "unknown";

    private static string ProgressLabel(InstallPlan plan) =>
        plan.Options?.TryGetValue("enshrouded_loader", out var loader) == true && loader is true
            ? "Installing Enshrouded mod loader"
            : "Installing Enshrouded mod";
}
