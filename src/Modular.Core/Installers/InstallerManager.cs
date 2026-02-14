using Microsoft.Extensions.Logging;
using Modular.Sdk.Installers;

namespace Modular.Core.Installers;

/// <summary>
/// Manages mod installation workflows by coordinating installer selection and execution.
/// </summary>
public class InstallerManager
{
    private readonly List<IModInstaller> _installers;
    private readonly ILogger<InstallerManager>? _logger;

    public InstallerManager(ILogger<InstallerManager>? logger = null)
    {
        _logger = logger;
        _installers = new List<IModInstaller>();

        // Register built-in installers (they'll create their own loggers)
        RegisterInstaller(new LooseFileInstaller());
        RegisterInstaller(new FomodInstaller());
        RegisterInstaller(new BepInExInstaller());
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
    /// Detects the best installer for an archive.
    /// </summary>
    public async Task<InstallerSelection?> SelectInstallerAsync(
        string archivePath,
        CancellationToken ct = default)
    {
        var detectionResults = new List<(IModInstaller Installer, InstallDetectionResult Result)>();

        foreach (var installer in _installers)
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
    /// Analyzes and creates an installation plan.
    /// </summary>
    public async Task<InstallPlan> CreateInstallPlanAsync(
        string archivePath,
        InstallContext context,
        IModInstaller? preferredInstaller = null,
        CancellationToken ct = default)
    {
        IModInstaller? installer = preferredInstaller;

        if (installer == null)
        {
            var selection = await SelectInstallerAsync(archivePath, ct);
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

        return await installer.InstallAsync(plan, progress, ct);
    }

    /// <summary>
    /// Installs a mod archive with automatic installer selection.
    /// </summary>
    public async Task<InstallResult> InstallModAsync(
        string archivePath,
        InstallContext context,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        // Select installer
        var selection = await SelectInstallerAsync(archivePath, ct);
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
        return await selection.Installer.InstallAsync(plan, progress, ct);
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
