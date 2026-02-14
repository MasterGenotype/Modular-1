using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Modular.Core.Plugins;

namespace Modular.Core.Diagnostics;

/// <summary>
/// Provides diagnostic and health check capabilities for the application.
/// </summary>
public class DiagnosticService
{
    private readonly PluginLoader _pluginLoader;
    private readonly ILogger<DiagnosticService>? _logger;

    public DiagnosticService(PluginLoader pluginLoader, ILogger<DiagnosticService>? logger = null)
    {
        _pluginLoader = pluginLoader;
        _logger = logger;
    }

    /// <summary>
    /// Runs a complete system health check.
    /// </summary>
    public async Task<SystemHealthReport> RunHealthCheckAsync(CancellationToken ct = default)
    {
        var report = new SystemHealthReport
        {
            Timestamp = DateTime.UtcNow,
            Checks = new List<HealthCheck>()
        };

        // Check plugin system
        report.Checks.Add(await CheckPluginSystemAsync(ct));

        // Check plugin integrity
        report.Checks.Add(await CheckPluginIntegrityAsync(ct));

        // Check for dependency issues
        report.Checks.Add(CheckDependencies());

        // Check disk space
        report.Checks.Add(CheckDiskSpace());

        // Determine overall health
        report.OverallStatus = report.Checks.All(c => c.Status == HealthStatus.Healthy)
            ? HealthStatus.Healthy
            : report.Checks.Any(c => c.Status == HealthStatus.Unhealthy)
                ? HealthStatus.Unhealthy
                : HealthStatus.Degraded;

        _logger?.LogInformation(
            "Health check completed: {Status} ({Healthy}/{Total} checks passed)",
            report.OverallStatus,
            report.Checks.Count(c => c.Status == HealthStatus.Healthy),
            report.Checks.Count);

        return report;
    }

    /// <summary>
    /// Checks the plugin system health.
    /// </summary>
    private async Task<HealthCheck> CheckPluginSystemAsync(CancellationToken ct)
    {
        var check = new HealthCheck
        {
            Name = "Plugin System",
            Description = "Verifies plugin loading and discovery"
        };

        try
        {
            var plugins = _pluginLoader.GetLoadedPlugins();
            var installers = _pluginLoader.GetAllInstallers();
            var enrichers = _pluginLoader.GetAllEnrichers();
            var uiExtensions = _pluginLoader.GetAllUiExtensions();

            check.Status = HealthStatus.Healthy;
            check.Message = $"Loaded {plugins.Count} plugins with {installers.Count} installers, {enrichers.Count} enrichers, {uiExtensions.Sum(g => g.Value.Count)} UI extensions";
            check.Data["plugin_count"] = plugins.Count;
            check.Data["installer_count"] = installers.Count;
            check.Data["enricher_count"] = enrichers.Count;
            check.Data["ui_extension_count"] = uiExtensions.Sum(g => g.Value.Count);
        }
        catch (Exception ex)
        {
            check.Status = HealthStatus.Unhealthy;
            check.Message = $"Plugin system error: {ex.Message}";
            check.Exception = ex;
            _logger?.LogError(ex, "Plugin system health check failed");
        }

        return await Task.FromResult(check);
    }

    /// <summary>
    /// Verifies plugin assembly integrity.
    /// </summary>
    private async Task<HealthCheck> CheckPluginIntegrityAsync(CancellationToken ct)
    {
        var check = new HealthCheck
        {
            Name = "Plugin Integrity",
            Description = "Validates plugin assemblies and manifests"
        };

        try
        {
            var plugins = _pluginLoader.GetLoadedPlugins();
            var issues = new List<string>();

            foreach (var plugin in plugins)
            {
                // Check if assembly is still valid
                try
                {
                    _ = plugin.Assembly.GetTypes();
                }
                catch (Exception ex)
                {
                    issues.Add($"{plugin.Manifest.Id}: Assembly validation failed - {ex.Message}");
                    continue;
                }

                // Check manifest consistency
                if (string.IsNullOrEmpty(plugin.Manifest.DisplayName))
                    issues.Add($"{plugin.Manifest.Id}: Missing display name");

                if (string.IsNullOrEmpty(plugin.Manifest.Version))
                    issues.Add($"{plugin.Manifest.Id}: Missing version");
            }

            if (issues.Count == 0)
            {
                check.Status = HealthStatus.Healthy;
                check.Message = $"All {plugins.Count} plugins passed integrity checks";
            }
            else
            {
                check.Status = HealthStatus.Degraded;
                check.Message = $"{issues.Count} integrity issue(s) found";
                check.Data["issues"] = issues;
            }
        }
        catch (Exception ex)
        {
            check.Status = HealthStatus.Unhealthy;
            check.Message = $"Integrity check failed: {ex.Message}";
            check.Exception = ex;
            _logger?.LogError(ex, "Plugin integrity check failed");
        }

        return await Task.FromResult(check);
    }

    /// <summary>
    /// Checks for dependency issues.
    /// </summary>
    private HealthCheck CheckDependencies()
    {
        var check = new HealthCheck
        {
            Name = "Dependencies",
            Description = "Verifies plugin dependencies are satisfied"
        };

        try
        {
            var plugins = _pluginLoader.GetLoadedPlugins();
            var pluginIds = plugins.Select(p => p.Manifest.Id).ToHashSet();
            var missingDeps = new List<string>();

            foreach (var plugin in plugins)
            {
                foreach (var depId in plugin.Manifest.Dependencies)
                {
                    if (!pluginIds.Contains(depId))
                    {
                        missingDeps.Add($"{plugin.Manifest.Id} requires missing plugin: {depId}");
                    }
                }
            }

            if (missingDeps.Count == 0)
            {
                check.Status = HealthStatus.Healthy;
                check.Message = "All plugin dependencies satisfied";
            }
            else
            {
                check.Status = HealthStatus.Degraded;
                check.Message = $"{missingDeps.Count} missing dependencies";
                check.Data["missing_dependencies"] = missingDeps;
            }
        }
        catch (Exception ex)
        {
            check.Status = HealthStatus.Unhealthy;
            check.Message = $"Dependency check failed: {ex.Message}";
            check.Exception = ex;
            _logger?.LogError(ex, "Dependency check failed");
        }

        return check;
    }

    /// <summary>
    /// Checks available disk space.
    /// </summary>
    private HealthCheck CheckDiskSpace()
    {
        var check = new HealthCheck
        {
            Name = "Disk Space",
            Description = "Verifies adequate disk space is available"
        };

        try
        {
            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "Modular");

            var drive = new DriveInfo(Path.GetPathRoot(configPath) ?? "/");
            var availableGB = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
            var totalGB = drive.TotalSize / (1024.0 * 1024 * 1024);
            var usedPercent = ((totalGB - availableGB) / totalGB) * 100;

            check.Data["available_gb"] = Math.Round(availableGB, 2);
            check.Data["total_gb"] = Math.Round(totalGB, 2);
            check.Data["used_percent"] = Math.Round(usedPercent, 2);

            if (availableGB < 1)
            {
                check.Status = HealthStatus.Unhealthy;
                check.Message = $"Critical: Only {availableGB:F2} GB available";
            }
            else if (availableGB < 5)
            {
                check.Status = HealthStatus.Degraded;
                check.Message = $"Warning: Only {availableGB:F2} GB available";
            }
            else
            {
                check.Status = HealthStatus.Healthy;
                check.Message = $"{availableGB:F2} GB available ({100 - usedPercent:F1}% free)";
            }
        }
        catch (Exception ex)
        {
            check.Status = HealthStatus.Unhealthy;
            check.Message = $"Disk space check failed: {ex.Message}";
            check.Exception = ex;
            _logger?.LogError(ex, "Disk space check failed");
        }

        return check;
    }

    /// <summary>
    /// Generates a detailed diagnostic report.
    /// </summary>
    public DiagnosticReport GenerateReport()
    {
        var report = new DiagnosticReport
        {
            Timestamp = DateTime.UtcNow,
            HostVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
            Runtime = $".NET {Environment.Version}",
            Platform = Environment.OSVersion.ToString(),
            WorkingDirectory = Environment.CurrentDirectory
        };

        // Gather plugin information
        var plugins = _pluginLoader.GetLoadedPlugins();
        report.LoadedPlugins = plugins.Select(p => new PluginInfo
        {
            Id = p.Manifest.Id,
            DisplayName = p.Manifest.DisplayName,
            Version = p.Manifest.Version,
            Author = p.Manifest.Author,
            AssemblyPath = p.Assembly.Location,
            InstallerCount = p.Installers.Count,
            EnricherCount = p.Enrichers.Count,
            UiExtensionCount = p.UiExtensions.Count
        }).ToList();

        // Gather component counts
        report.TotalInstallers = _pluginLoader.GetAllInstallers().Count;
        report.TotalEnrichers = _pluginLoader.GetAllEnrichers().Count;
        report.TotalUiExtensions = _pluginLoader.GetAllUiExtensions().Sum(g => g.Value.Count);

        _logger?.LogInformation(
            "Generated diagnostic report: {Plugins} plugins, {Installers} installers, {Enrichers} enrichers",
            report.LoadedPlugins.Count, report.TotalInstallers, report.TotalEnrichers);

        return report;
    }

    /// <summary>
    /// Validates a plugin manifest file.
    /// </summary>
    public PluginValidationResult ValidatePlugin(string manifestPath)
    {
        var result = new PluginValidationResult
        {
            ManifestPath = manifestPath,
            IsValid = true
        };

        try
        {
            if (!File.Exists(manifestPath))
            {
                result.IsValid = false;
                result.Errors.Add("Manifest file not found");
                return result;
            }

            // Load and parse manifest
            var json = File.ReadAllText(manifestPath);
            var manifest = System.Text.Json.JsonSerializer.Deserialize<PluginManifest>(json);

            if (manifest == null)
            {
                result.IsValid = false;
                result.Errors.Add("Failed to parse manifest JSON");
                return result;
            }

            // Validate required fields
            if (string.IsNullOrEmpty(manifest.Id))
                result.Errors.Add("Missing required field: id");

            if (string.IsNullOrEmpty(manifest.Version))
                result.Errors.Add("Missing required field: version");

            if (string.IsNullOrEmpty(manifest.EntryAssembly))
                result.Errors.Add("Missing required field: entry_assembly");

            // Check if entry assembly exists
            var pluginDir = Path.GetDirectoryName(manifestPath);
            if (!string.IsNullOrEmpty(pluginDir) && !string.IsNullOrEmpty(manifest.EntryAssembly))
            {
                var assemblyPath = Path.Combine(pluginDir, manifest.EntryAssembly);
                if (!File.Exists(assemblyPath))
                    result.Warnings.Add($"Entry assembly not found: {manifest.EntryAssembly}");
            }

            // Validate version format
            if (!string.IsNullOrEmpty(manifest.Version) && !Version.TryParse(manifest.Version, out _))
                result.Warnings.Add($"Invalid version format: {manifest.Version}");

            result.IsValid = result.Errors.Count == 0;
            result.Manifest = manifest;
        }
        catch (Exception ex)
        {
            result.IsValid = false;
            result.Errors.Add($"Validation failed: {ex.Message}");
            _logger?.LogError(ex, "Plugin validation failed for {Path}", manifestPath);
        }

        return result;
    }
}

/// <summary>
/// Overall system health report.
/// </summary>
public class SystemHealthReport
{
    public DateTime Timestamp { get; set; }
    public HealthStatus OverallStatus { get; set; }
    public List<HealthCheck> Checks { get; set; } = new();
}

/// <summary>
/// Individual health check result.
/// </summary>
public class HealthCheck
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public HealthStatus Status { get; set; }
    public string Message { get; set; } = string.Empty;
    public Dictionary<string, object> Data { get; set; } = new();
    public Exception? Exception { get; set; }
}

/// <summary>
/// Health status levels.
/// </summary>
public enum HealthStatus
{
    Healthy,
    Degraded,
    Unhealthy
}

/// <summary>
/// Comprehensive diagnostic report.
/// </summary>
public class DiagnosticReport
{
    public DateTime Timestamp { get; set; }
    public string HostVersion { get; set; } = string.Empty;
    public string Runtime { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public List<PluginInfo> LoadedPlugins { get; set; } = new();
    public int TotalInstallers { get; set; }
    public int TotalEnrichers { get; set; }
    public int TotalUiExtensions { get; set; }
}

/// <summary>
/// Plugin information for diagnostic report.
/// </summary>
public class PluginInfo
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string? Author { get; set; }
    public string AssemblyPath { get; set; } = string.Empty;
    public int InstallerCount { get; set; }
    public int EnricherCount { get; set; }
    public int UiExtensionCount { get; set; }
}

/// <summary>
/// Result of plugin validation.
/// </summary>
public class PluginValidationResult
{
    public string ManifestPath { get; set; } = string.Empty;
    public bool IsValid { get; set; }
    public PluginManifest? Manifest { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}
