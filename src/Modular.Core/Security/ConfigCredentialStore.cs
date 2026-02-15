using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Modular.Core.Security;

/// <summary>
/// Fallback credential store that uses a JSON config file.
/// Used when OS-level credential storage is unavailable.
/// </summary>
/// <remarks>
/// WARNING: This implementation stores credentials in plaintext and should only
/// be used as a fallback when secure storage is not available. Users will be
/// warned at startup if this store is being used.
/// </remarks>
public class ConfigCredentialStore : ICredentialStore
{
    private readonly string _credentialsPath;
    private readonly ILogger<ConfigCredentialStore>? _logger;
    private readonly object _lock = new();
    private Dictionary<string, string> _credentials = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Creates a config-based credential store.
    /// </summary>
    /// <param name="credentialsPath">Path to the credentials JSON file</param>
    /// <param name="logger">Optional logger</param>
    public ConfigCredentialStore(string credentialsPath, ILogger<ConfigCredentialStore>? logger = null)
    {
        _credentialsPath = credentialsPath;
        _logger = logger;

        // Warn that this is not secure storage
        _logger?.LogWarning(
            "Using plaintext credential storage at {Path}. " +
            "Consider using OS credential manager for better security.",
            credentialsPath);

        LoadSync();
    }

    /// <inheritdoc />
    public bool IsSecure => false;

    /// <inheritdoc />
    public Task SetCredentialAsync(string key, string value, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _credentials[key] = value;
        }
        return SaveAsync(ct);
    }

    /// <inheritdoc />
    public Task<string?> GetCredentialAsync(string key, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_credentials.TryGetValue(key, out var value) ? value : null);
        }
    }

    /// <inheritdoc />
    public async Task<bool> RemoveCredentialAsync(string key, CancellationToken ct = default)
    {
        bool removed;
        lock (_lock)
        {
            removed = _credentials.Remove(key);
        }

        if (removed)
        {
            await SaveAsync(ct);
        }

        return removed;
    }

    /// <inheritdoc />
    public Task<bool> HasCredentialAsync(string key, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_credentials.ContainsKey(key));
        }
    }

    private void LoadSync()
    {
        if (!File.Exists(_credentialsPath))
            return;

        try
        {
            var json = File.ReadAllText(_credentialsPath);
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (data != null)
            {
                lock (_lock)
                {
                    _credentials = data;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load credentials from {Path}", _credentialsPath);
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        Dictionary<string, string> snapshot;
        lock (_lock)
        {
            snapshot = new Dictionary<string, string>(_credentials);
        }

        try
        {
            var directory = Path.GetDirectoryName(_credentialsPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            await File.WriteAllTextAsync(_credentialsPath, json, ct);

            // Set restrictive permissions on Unix
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(_credentialsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch
                {
                    // Ignore permission errors on unsupported filesystems
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save credentials to {Path}", _credentialsPath);
        }
    }
}
