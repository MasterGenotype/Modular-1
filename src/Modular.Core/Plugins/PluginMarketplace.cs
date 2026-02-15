using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Modular.Core.Plugins;

/// <summary>
/// Plugin marketplace for discovering and installing community plugins.
/// </summary>
public class PluginMarketplace
{
    private readonly HttpClient _httpClient;
    private readonly string _pluginsDirectory;
    private readonly ILogger<PluginMarketplace>? _logger;

    public PluginMarketplace(
        HttpClient httpClient,
        string pluginsDirectory,
        ILogger<PluginMarketplace>? logger = null)
    {
        _httpClient = httpClient;
        _pluginsDirectory = pluginsDirectory;
        _logger = logger;
    }

    /// <summary>
    /// Fetches the plugin index from a remote URL.
    /// </summary>
    public async Task<PluginIndex?> FetchIndexAsync(string indexUrl, CancellationToken ct = default)
    {
        try
        {
            var json = await _httpClient.GetStringAsync(indexUrl, ct);
            return JsonSerializer.Deserialize<PluginIndex>(json);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to fetch plugin index from {Url}", indexUrl);
            return null;
        }
    }

    /// <summary>
    /// Downloads and installs a plugin.
    /// </summary>
    public async Task<PluginInstallResult> InstallPluginAsync(
        PluginIndexEntry plugin,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var result = new PluginInstallResult { PluginId = plugin.Id };

        try
        {
            // Download plugin archive
            _logger?.LogInformation("Downloading plugin {Name} from {Url}", plugin.Name, plugin.DownloadUrl);
            
            var tempPath = Path.Combine(Path.GetTempPath(), $"{plugin.Id}.zip");
            await DownloadFileAsync(plugin.DownloadUrl, tempPath, progress, ct);

            // Verify hash
            if (!string.IsNullOrEmpty(plugin.Sha256))
            {
                var actualHash = await ComputeSha256Async(tempPath, ct);
                if (!actualHash.Equals(plugin.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    result.Success = false;
                    result.Error = $"Hash mismatch: expected {plugin.Sha256}, got {actualHash}";
                    File.Delete(tempPath);
                    return result;
                }
            }

            // Extract to plugins directory
            var targetDir = Path.Combine(_pluginsDirectory, plugin.Id);
            if (Directory.Exists(targetDir))
                Directory.Delete(targetDir, true);

            Directory.CreateDirectory(targetDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempPath, targetDir);

            // Cleanup
            File.Delete(tempPath);

            result.Success = true;
            result.InstalledPath = targetDir;
            _logger?.LogInformation("Successfully installed plugin {Name}", plugin.Name);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _logger?.LogError(ex, "Failed to install plugin {Name}", plugin.Name);
        }

        return result;
    }

    /// <summary>
    /// Checks for plugin updates.
    /// </summary>
    public Task<List<PluginUpdate>> CheckUpdatesAsync(
        PluginIndex index,
        List<PluginManifest> installedPlugins,
        CancellationToken ct = default)
    {
        var updates = new List<PluginUpdate>();

        foreach (var installed in installedPlugins)
        {
            var indexEntry = index.Plugins.FirstOrDefault(p => p.Id == installed.Id);
            if (indexEntry == null)
                continue;

            if (Version.TryParse(installed.Version, out var installedVer) &&
                Version.TryParse(indexEntry.Version, out var availableVer))
            {
                if (availableVer > installedVer)
                {
                    updates.Add(new PluginUpdate
                    {
                        PluginId = installed.Id,
                        PluginName = installed.DisplayName,
                        CurrentVersion = installed.Version,
                        AvailableVersion = indexEntry.Version,
                        IndexEntry = indexEntry
                    });
                }
            }
        }

        return Task.FromResult(updates);
    }

    /// <summary>
    /// Uninstalls a plugin.
    /// </summary>
    public Task<bool> UninstallPluginAsync(string pluginId)
    {
        try
        {
            var pluginDir = Path.Combine(_pluginsDirectory, pluginId);
            if (Directory.Exists(pluginDir))
            {
                Directory.Delete(pluginDir, true);
                _logger?.LogInformation("Uninstalled plugin {PluginId}", pluginId);
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to uninstall plugin {PluginId}", pluginId);
            return Task.FromResult(false);
        }
    }

    private async Task DownloadFileAsync(
        string url,
        string outputPath,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0;
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = File.Create(outputPath);

        var buffer = new byte[8192];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
            totalRead += bytesRead;

            if (totalBytes > 0)
                progress?.Report((double)totalRead / totalBytes * 100);
        }
    }

    private async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        using var sha256 = SHA256.Create();
        await using var stream = File.OpenRead(filePath);
        var hashBytes = await sha256.ComputeHashAsync(stream, ct);
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }
}

/// <summary>
/// Plugin marketplace index.
/// </summary>
public class PluginIndex
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("plugins")]
    public List<PluginIndexEntry> Plugins { get; set; } = new();
}

/// <summary>
/// Plugin entry in marketplace index.
/// </summary>
public class PluginIndexEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("download_url")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("min_host_version")]
    public string? MinHostVersion { get; set; }

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; set; }

    [JsonPropertyName("homepage_url")]
    public string? HomepageUrl { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();
}

/// <summary>
/// Result of plugin installation.
/// </summary>
public class PluginInstallResult
{
    public bool Success { get; set; }
    public string PluginId { get; set; } = string.Empty;
    public string? InstalledPath { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Available plugin update.
/// </summary>
public class PluginUpdate
{
    public string PluginId { get; set; } = string.Empty;
    public string PluginName { get; set; } = string.Empty;
    public string CurrentVersion { get; set; } = string.Empty;
    public string AvailableVersion { get; set; } = string.Empty;
    public PluginIndexEntry? IndexEntry { get; set; }
}
