using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Modular.Core.Telemetry;

/// <summary>
/// Privacy-respecting telemetry service with local-first storage and opt-in collection.
/// </summary>
public class TelemetryService
{
    private readonly string _telemetryPath;
    private readonly ILogger<TelemetryService>? _logger;
    private readonly TelemetryConfig _config;
    private readonly object _lock = new();

    public TelemetryService(
        string telemetryPath,
        TelemetryConfig? config = null,
        ILogger<TelemetryService>? logger = null)
    {
        _telemetryPath = telemetryPath;
        _config = config ?? new TelemetryConfig();
        _logger = logger;

        Directory.CreateDirectory(_telemetryPath);
    }

    /// <summary>
    /// Records a telemetry event. Only recorded if telemetry is enabled.
    /// </summary>
    public void RecordEvent(TelemetryEvent evt)
    {
        if (!_config.Enabled)
            return;

        try
        {
            lock (_lock)
            {
                evt.Timestamp = DateTime.UtcNow;
                evt.SessionId = _config.SessionId;

                // Anonymize if configured
                if (_config.AnonymizeData)
                {
                    evt = AnonymizeEvent(evt);
                }

                // Store locally
                var eventPath = GetEventPath(evt.Timestamp);
                var events = LoadEventsFromFile(eventPath);
                events.Add(evt);

                SaveEventsToFile(eventPath, events);

                _logger?.LogDebug(
                    "Recorded telemetry event: {Type} ({Category})",
                    evt.EventType, evt.Category);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to record telemetry event");
        }
    }

    /// <summary>
    /// Records a plugin crash event.
    /// </summary>
    public void RecordPluginCrash(string pluginId, Exception exception)
    {
        RecordEvent(new TelemetryEvent
        {
            EventType = "plugin_crash",
            Category = "error",
            Data = new Dictionary<string, object>
            {
                ["plugin_id"] = pluginId,
                ["exception_type"] = exception.GetType().Name,
                ["message"] = exception.Message,
                ["stack_trace"] = exception.StackTrace ?? "N/A"
            }
        });
    }

    /// <summary>
    /// Records installer success/failure.
    /// </summary>
    public void RecordInstallerResult(string installerId, bool success, TimeSpan duration)
    {
        RecordEvent(new TelemetryEvent
        {
            EventType = "installer_execution",
            Category = "performance",
            Data = new Dictionary<string, object>
            {
                ["installer_id"] = installerId,
                ["success"] = success,
                ["duration_ms"] = duration.TotalMilliseconds
            }
        });
    }

    /// <summary>
    /// Records download statistics.
    /// </summary>
    public void RecordDownload(string backend, long sizeBytes, TimeSpan duration, bool success)
    {
        RecordEvent(new TelemetryEvent
        {
            EventType = "download_completed",
            Category = "usage",
            Data = new Dictionary<string, object>
            {
                ["backend"] = backend,
                ["size_bytes"] = sizeBytes,
                ["duration_ms"] = duration.TotalMilliseconds,
                ["success"] = success
            }
        });
    }

    /// <summary>
    /// Gets telemetry summary for a date range.
    /// </summary>
    public TelemetrySummary GetSummary(DateTime? startDate = null, DateTime? endDate = null)
    {
        startDate ??= DateTime.UtcNow.AddDays(-30);
        endDate ??= DateTime.UtcNow;

        var summary = new TelemetrySummary
        {
            StartDate = startDate.Value,
            EndDate = endDate.Value
        };

        try
        {
            lock (_lock)
            {
                var allEvents = LoadEventsInRange(startDate.Value, endDate.Value);

                summary.TotalEvents = allEvents.Count;
                summary.EventsByType = allEvents
                    .GroupBy(e => e.EventType)
                    .ToDictionary(g => g.Key, g => g.Count());

                summary.EventsByCategory = allEvents
                    .GroupBy(e => e.Category)
                    .ToDictionary(g => g.Key, g => g.Count());

                // Plugin crashes
                summary.PluginCrashes = allEvents
                    .Where(e => e.EventType == "plugin_crash")
                    .Count();

                // Installer stats
                var installerEvents = allEvents
                    .Where(e => e.EventType == "installer_execution")
                    .ToList();

                summary.InstallerSuccesses = installerEvents
                    .Count(e => e.Data.ContainsKey("success") && (bool)e.Data["success"]);
                summary.InstallerFailures = installerEvents.Count - summary.InstallerSuccesses;

                // Download stats
                var downloadEvents = allEvents
                    .Where(e => e.EventType == "download_completed")
                    .ToList();

                summary.TotalDownloads = downloadEvents.Count;
                summary.TotalBytesDownloaded = downloadEvents
                    .Where(e => e.Data.ContainsKey("size_bytes"))
                    .Sum(e => Convert.ToInt64(e.Data["size_bytes"]));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to generate telemetry summary");
        }

        return summary;
    }

    /// <summary>
    /// Clears all telemetry data.
    /// </summary>
    public void ClearData()
    {
        try
        {
            lock (_lock)
            {
                if (Directory.Exists(_telemetryPath))
                {
                    Directory.Delete(_telemetryPath, true);
                    Directory.CreateDirectory(_telemetryPath);
                }

                _logger?.LogInformation("Cleared all telemetry data");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to clear telemetry data");
        }
    }

    /// <summary>
    /// Exports telemetry data for a date range.
    /// </summary>
    public async Task<bool> ExportDataAsync(string outputPath, DateTime? startDate = null, DateTime? endDate = null)
    {
        try
        {
            startDate ??= DateTime.UtcNow.AddDays(-30);
            endDate ??= DateTime.UtcNow;

            lock (_lock)
            {
                var events = LoadEventsInRange(startDate.Value, endDate.Value);

                var export = new TelemetryExport
                {
                    ExportedAt = DateTime.UtcNow,
                    StartDate = startDate.Value,
                    EndDate = endDate.Value,
                    Events = events
                };

                var json = JsonSerializer.Serialize(export, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                File.WriteAllText(outputPath, json);
            }

            _logger?.LogInformation("Exported telemetry data to {Path}", outputPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to export telemetry data");
            return false;
        }
    }

    private TelemetryEvent AnonymizeEvent(TelemetryEvent evt)
    {
        // Remove potentially identifying information
        var anonymized = new TelemetryEvent
        {
            EventType = evt.EventType,
            Category = evt.Category,
            Timestamp = evt.Timestamp,
            SessionId = HashString(evt.SessionId), // Hash session ID
            Data = new Dictionary<string, object>()
        };

        // Anonymize data fields
        foreach (var (key, value) in evt.Data)
        {
            // Keep only aggregate/non-identifying data
            if (key.Contains("id", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("path", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("user", StringComparison.OrdinalIgnoreCase))
            {
                continue; // Skip potentially identifying fields
            }

            anonymized.Data[key] = value;
        }

        return anonymized;
    }

    private string HashString(string input)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToBase64String(hash).Substring(0, 16);
    }

    private string GetEventPath(DateTime date)
    {
        return Path.Combine(_telemetryPath, $"telemetry-{date:yyyy-MM-dd}.json");
    }

    private List<TelemetryEvent> LoadEventsFromFile(string path)
    {
        if (!File.Exists(path))
            return new List<TelemetryEvent>();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<TelemetryEvent>>(json) ?? new List<TelemetryEvent>();
        }
        catch
        {
            return new List<TelemetryEvent>();
        }
    }

    private void SaveEventsToFile(string path, List<TelemetryEvent> events)
    {
        var json = JsonSerializer.Serialize(events);
        File.WriteAllText(path, json);
    }

    private List<TelemetryEvent> LoadEventsInRange(DateTime startDate, DateTime endDate)
    {
        var events = new List<TelemetryEvent>();
        var currentDate = startDate.Date;

        while (currentDate <= endDate.Date)
        {
            var path = GetEventPath(currentDate);
            events.AddRange(LoadEventsFromFile(path));
            currentDate = currentDate.AddDays(1);
        }

        return events.Where(e => e.Timestamp >= startDate && e.Timestamp <= endDate).ToList();
    }
}

/// <summary>
/// Telemetry configuration.
/// </summary>
public class TelemetryConfig
{
    /// <summary>
    /// Whether telemetry is enabled (opt-in).
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Whether to anonymize collected data.
    /// </summary>
    public bool AnonymizeData { get; set; } = true;

    /// <summary>
    /// Session identifier.
    /// </summary>
    public string SessionId { get; set; } = Guid.NewGuid().ToString();
}

/// <summary>
/// A telemetry event.
/// </summary>
public class TelemetryEvent
{
    public string EventType { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public Dictionary<string, object> Data { get; set; } = new();
}

/// <summary>
/// Summary of telemetry data.
/// </summary>
public class TelemetrySummary
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public int TotalEvents { get; set; }
    public Dictionary<string, int> EventsByType { get; set; } = new();
    public Dictionary<string, int> EventsByCategory { get; set; } = new();
    public int PluginCrashes { get; set; }
    public int InstallerSuccesses { get; set; }
    public int InstallerFailures { get; set; }
    public int TotalDownloads { get; set; }
    public long TotalBytesDownloaded { get; set; }
}

/// <summary>
/// Telemetry data export.
/// </summary>
public class TelemetryExport
{
    public DateTime ExportedAt { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public List<TelemetryEvent> Events { get; set; } = new();
}
