using Microsoft.Extensions.Logging;
using Modular.Core.Archives;
using Modular.Core.Installers.Cyberpunk;
using Modular.Core.Installers.FF7Remake;
using Modular.Core.Installers.BaldursGate3;
using Modular.Core.Installers.HorizonZeroDawn;
using Modular.Core.Installers.Steam;
using Modular.Core.Telemetry;
using Modular.Sdk.Archives;
using Modular.Sdk.Installers;
using Modular.Sdk.Modules;

namespace Modular.Core.Installers;

/// <summary>
/// Manages mod installation workflows by coordinating installer selection and execution.
/// </summary>
public class InstallerManager
{
    private readonly List<IModInstaller> _installers;
    private readonly HashSet<string> _absorbedModules = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<InstallerManager>? _logger;
    private readonly TelemetryService? _telemetry;

    public InstallerManager(
        IArchiveReaderFactory? archiveReaderFactory = null,
        ILogger<InstallerManager>? logger = null,
        TelemetryService? telemetry = null)
    {
        _logger = logger;
        _telemetry = telemetry;
        _installers = new List<IModInstaller>();

        var factory = archiveReaderFactory ?? new ArchiveReaderFactory();

        // Register built-in installers with shared archive reader factory
        RegisterInstaller(new LooseFileInstaller(factory));
        RegisterInstaller(new FomodInstaller(factory));
        RegisterInstaller(new BepInExInstaller(factory));
        RegisterInstaller(new SteamModInstaller(factory));
        RegisterInstaller(new UnrealPakInstaller(factory));
        RegisterInstaller(new CyberpunkModInstaller(factory));
        RegisterInstaller(new FF7RModInstaller(factory));
        RegisterInstaller(new HZDModInstaller(factory));
        RegisterInstaller(new BG3ModInstaller(factory));
    }

    /// <summary>
    /// Registers an installer.
    /// </summary>
    public void RegisterInstaller(IModInstaller installer)
    {
        _installers.Add(installer);
        _logger?.LogInformation("Registered installer: {InstallerId} (Priority: {Priority})",
            installer.InstallerId, installer.Priority);
    }

    /// <summary>
    /// Registers multiple installers.
    /// </summary>
    public void RegisterInstallers(IEnumerable<IModInstaller> installers)
    {
        foreach (var installer in installers)
        {
            RegisterInstaller(installer);
        }
    }

    /// <summary>
    /// Detects the best installer for an archive, scoped to a game and, when one was
    /// resolved, to the installers its <see cref="IGameModule"/> contributes.
    /// </summary>
    /// <param name="archivePath">Archive to analyse.</param>
    /// <param name="gameId">
    /// Game identifier (slug or AppID). Game-specific installers that do not claim it
    /// are excluded before any content analysis runs.
    /// </param>
    /// <param name="module">
    /// The resolved game module, when the caller went through game resolution. Its
    /// installers are always candidates, even if they declare no matching
    /// <see cref="IModInstaller.SupportedGameIds"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The winning installer, or <c>null</c> when nothing can handle the archive.</returns>
    /// <remarks>
    /// Universal installers (null or empty <see cref="IModInstaller.SupportedGameIds"/>)
    /// are always considered. With neither a game id nor a module, every registered
    /// installer is a candidate.
    /// </remarks>
    public async Task<InstallerSelection?> SelectInstallerAsync(
        string archivePath,
        string? gameId = null,
        IGameModule? module = null,
        CancellationToken ct = default)
    {
        var detectionResults = new List<(IModInstaller Installer, InstallDetectionResult Result)>();

        var candidates = BuildCandidates(gameId, module);

        foreach (var installer in candidates)
        {
            try
            {
                var result = await installer.DetectAsync(archivePath, ct);
                if (result.CanHandle)
                {
                    detectionResults.Add((installer, result));
                    _logger?.LogDebug(
                        "Installer {InstallerId} can handle {Archive}: {Confidence:P0} - {Reason}",
                        installer.InstallerId, Path.GetFileName(archivePath),
                        result.Confidence, result.Reason);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during detection with installer {InstallerId}",
                    installer.InstallerId);
            }
        }

        if (detectionResults.Count == 0)
        {
            _logger?.LogWarning("No installer found for {Archive}", Path.GetFileName(archivePath));
            return null;
        }

        // Select installer with highest priority, then highest confidence
        var selected = detectionResults
            .OrderByDescending(r => r.Installer.Priority)
            .ThenByDescending(r => r.Result.Confidence)
            .First();

        _logger?.LogInformation(
            "Selected installer {InstallerId} for {Archive} (Confidence: {Confidence:P0})",
            selected.Installer.InstallerId, Path.GetFileName(archivePath),
            selected.Result.Confidence);

        return new InstallerSelection
        {
            Installer = selected.Installer,
            DetectionResult = selected.Result,
            AlternativeInstallers = detectionResults
                .Where(r => r.Installer != selected.Installer)
                .Select(r => r.Installer)
                .ToList()
        };
    }

    /// <summary>
    /// Builds the candidate installer pool for a selection.
    /// </summary>
    /// <param name="gameId">Game identifier, or <c>null</c> when unknown.</param>
    /// <param name="module">Resolved game module, or <c>null</c> when unknown.</param>
    /// <returns>The installers that may be asked whether they can handle the archive.</returns>
    /// <remarks>
    /// The pool is the union of the universal installers, the resolved module's own
    /// installers, and any installer (built-in or plugin-supplied) that declares
    /// <paramref name="gameId"/>.
    /// </remarks>
    private List<IModInstaller> BuildCandidates(string? gameId, IGameModule? module)
    {
        if (string.IsNullOrEmpty(gameId) && module == null)
            return _installers.ToList();

        var moduleInstallerIds = module == null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : AbsorbModuleInstallers(module);

        return _installers
            .Where(i =>
                IsUniversal(i) ||
                moduleInstallerIds.Contains(i.InstallerId) ||
                ClaimsGame(i, gameId))
            .ToList();
    }

    /// <summary>
    /// Registers a module's installers if they are not already known, and returns
    /// their ids.
    /// </summary>
    /// <param name="module">The module whose installers to absorb.</param>
    /// <returns>Installer ids contributed by the module.</returns>
    /// <remarks>
    /// Plugin-supplied modules bring installers the manager has never seen.
    /// Registering them here keeps a single registry, which matters because
    /// <see cref="ExecuteInstallAsync"/> looks a plan's installer up by id.
    /// </remarks>
    private HashSet<string> AbsorbModuleInstallers(IGameModule module)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var alreadyAbsorbed = !_absorbedModules.Add(module.ModuleId);

        IReadOnlyList<IModInstaller> moduleInstallers;
        try
        {
            moduleInstallers = module.GetInstallers();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex, "Game module {ModuleId} threw while listing installers", module.ModuleId);
            return ids;
        }

        foreach (var installer in moduleInstallers)
        {
            ids.Add(installer.InstallerId);

            if (alreadyAbsorbed ||
                _installers.Any(i => string.Equals(
                    i.InstallerId, installer.InstallerId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            RegisterInstaller(installer);
        }

        return ids;
    }

    private static bool IsUniversal(IModInstaller installer) =>
        installer.SupportedGameIds == null || installer.SupportedGameIds.Count == 0;

    private static bool ClaimsGame(IModInstaller installer, string? gameId) =>
        !string.IsNullOrEmpty(gameId) &&
        installer.SupportedGameIds != null &&
        installer.SupportedGameIds.Any(g =>
            string.Equals(g, gameId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Analyzes and creates an installation plan.
    /// </summary>
    public async Task<InstallPlan> CreateInstallPlanAsync(
        string archivePath,
        InstallContext context,
        IModInstaller? preferredInstaller = null,
        IGameModule? module = null,
        CancellationToken ct = default)
    {
        IModInstaller? installer = preferredInstaller;

        if (installer == null)
        {
            var selection = await SelectInstallerAsync(archivePath, context.GameId, module, ct);
            if (selection == null)
            {
                throw new InvalidOperationException($"No installer found for {archivePath}");
            }
            installer = selection.Installer;
        }

        _logger?.LogInformation("Creating install plan with {InstallerId}", installer.InstallerId);

        return await installer.AnalyzeAsync(archivePath, context, ct);
    }

    /// <summary>
    /// Executes an installation plan.
    /// </summary>
    public async Task<InstallResult> ExecuteInstallAsync(
        InstallPlan plan,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var installer = _installers.FirstOrDefault(i => i.InstallerId == plan.InstallerId);
        if (installer == null)
        {
            throw new InvalidOperationException(
                $"Installer {plan.InstallerId} not found. Cannot execute plan.");
        }

        _logger?.LogInformation(
            "Executing installation with {InstallerId}: {FileCount} files, {Size:N0} bytes",
            installer.InstallerId, plan.Operations.Count, plan.TotalBytes);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await installer.InstallAsync(plan, progress, ct);
        stopwatch.Stop();

        _telemetry?.RecordInstallerResult(installer.InstallerId, result.Success, stopwatch.Elapsed);

        return result;
    }

    /// <summary>
    /// Installs a mod archive with automatic installer selection.
    /// </summary>
    public async Task<InstallResult> InstallModAsync(
        string archivePath,
        InstallContext context,
        IProgress<InstallProgress>? progress = null,
        IGameModule? module = null,
        CancellationToken ct = default)
    {
        // Select installer
        var selection = await SelectInstallerAsync(archivePath, context.GameId, module, ct);
        if (selection == null)
        {
            return new InstallResult
            {
                Success = false,
                Error = "No suitable installer found for this archive"
            };
        }

        // Create plan
        var plan = await selection.Installer.AnalyzeAsync(archivePath, context, ct);

        // Execute
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await selection.Installer.InstallAsync(plan, progress, ct);
        stopwatch.Stop();

        _telemetry?.RecordInstallerResult(selection.Installer.InstallerId, result.Success, stopwatch.Elapsed);

        return result;
    }

    /// <summary>
    /// Gets all registered installers.
    /// </summary>
    public IReadOnlyList<IModInstaller> GetInstallers() => _installers.AsReadOnly();

    /// <summary>
    /// Gets an installer by ID.
    /// </summary>
    public IModInstaller? GetInstaller(string installerId) =>
        _installers.FirstOrDefault(i => i.InstallerId == installerId);
}

/// <summary>
/// Result of installer selection.
/// </summary>
public class InstallerSelection
{
    /// <summary>
    /// Selected installer.
    /// </summary>
    public required IModInstaller Installer { get; init; }

    /// <summary>
    /// Detection result for the selected installer.
    /// </summary>
    public required InstallDetectionResult DetectionResult { get; init; }

    /// <summary>
    /// Alternative installers that can handle the archive.
    /// </summary>
    public List<IModInstaller> AlternativeInstallers { get; init; } = new();
}
