using System.Diagnostics;
using System.Text.Json;

namespace Modular.Core.Security;

/// <summary>
/// Stores credentials that must not sit in plain config (Steam refresh tokens and similar).
/// </summary>
public interface ISecretStore
{
    /// <summary>Human-readable name of the backing store, for status output.</summary>
    string Description { get; }

    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string value, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}

/// <summary>
/// Secret Service (libsecret) through the <c>secret-tool</c> CLI. Works with KWallet/ksecretd and
/// gnome-keyring. Throws <see cref="SecretStoreUnavailableException"/> when the tool or the
/// service is missing, so <see cref="DefaultSecretStore"/> can fall back.
/// </summary>
public sealed class LibSecretStore : ISecretStore
{
    private const string Service = "modular";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public string Description => "Secret Service (secret-tool)";

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var (code, stdout, stderr) = await RunAsync(["lookup", "service", Service, "key", key], null, ct);
        if (code == 0)
            return stdout.Length == 0 ? null : stdout;
        // secret-tool exits 1 with no stderr when the item does not exist.
        if (string.IsNullOrWhiteSpace(stderr))
            return null;
        throw new SecretStoreUnavailableException(stderr.Trim());
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        var (code, _, stderr) = await RunAsync(
            ["store", $"--label=Modular: {key}", "service", Service, "key", key], value, ct);
        if (code != 0)
            throw new SecretStoreUnavailableException(stderr.Trim());
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var (code, _, stderr) = await RunAsync(["clear", "service", Service, "key", key], null, ct);
        if (code != 0 && !string.IsNullOrWhiteSpace(stderr))
            throw new SecretStoreUnavailableException(stderr.Trim());
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunAsync(
        string[] args, string? stdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("secret-tool")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new SecretStoreUnavailableException("secret-tool did not start");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new SecretStoreUnavailableException($"secret-tool not available: {ex.Message}");
        }

        using (process)
        {
            if (stdin is not null)
                await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                throw new SecretStoreUnavailableException("secret-tool timed out (locked wallet waiting for a prompt?)");
            }
            return (process.ExitCode, await stdoutTask, await stderrTask);
        }
    }
}

/// <summary>
/// Fallback store: a JSON file readable only by the user (mode 0600). Used when no Secret
/// Service is running, e.g. in a gamescope session without a wallet daemon.
/// </summary>
public sealed class FileSecretStore : ISecretStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FileSecretStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "Modular", "secrets.json");
    }

    public string Description => $"file {_path} (0600)";

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var all = await LoadAsync(ct);
        return all.TryGetValue(key, out var v) ? v : null;
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await LoadAsync(ct);
            all[key] = value;
            await SaveAsync(all, ct);
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await LoadAsync(ct);
            if (all.Remove(key))
                await SaveAsync(all, ct);
        }
        finally { _lock.Release(); }
    }

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
            return new();
        await using var s = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(s, cancellationToken: ct) ?? new();
    }

    private async Task SaveAsync(Dictionary<string, string> all, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; // never world-readable, even briefly
        await using (var s = new FileStream(tmp, options))
            await JsonSerializer.SerializeAsync(s, all, cancellationToken: ct);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, _path, overwrite: true);
    }
}

/// <summary>
/// Secret Service first, the 0600 file when the service is unavailable. Reads check both, so a
/// token saved through the fallback is still found once a wallet appears.
/// </summary>
public sealed class DefaultSecretStore : ISecretStore
{
    private readonly ISecretStore _primary;
    private readonly ISecretStore _fallback;

    public DefaultSecretStore(ISecretStore? primary = null, ISecretStore? fallback = null)
    {
        _primary = primary ?? new LibSecretStore();
        _fallback = fallback ?? new FileSecretStore();
    }

    /// <summary>Set after a write: which store actually holds the value.</summary>
    public string Description { get; private set; } = "Secret Service, file fallback";

    /// <summary>Why the primary store was skipped on the last operation, if it was.</summary>
    public string? LastFallbackReason { get; private set; }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var v = await _primary.GetAsync(key, ct);
            if (v is not null)
            {
                Description = _primary.Description;
                return v;
            }
        }
        catch (SecretStoreUnavailableException ex)
        {
            LastFallbackReason = ex.Message;
        }
        var f = await _fallback.GetAsync(key, ct);
        if (f is not null)
            Description = _fallback.Description;
        return f;
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        try
        {
            await _primary.SetAsync(key, value, ct);
            Description = _primary.Description;
            await _fallback.DeleteAsync(key, ct); // don't leave a stale copy in the file
            return;
        }
        catch (SecretStoreUnavailableException ex)
        {
            LastFallbackReason = ex.Message;
        }
        await _fallback.SetAsync(key, value, ct);
        Description = _fallback.Description;
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        try { await _primary.DeleteAsync(key, ct); }
        catch (SecretStoreUnavailableException ex) { LastFallbackReason = ex.Message; }
        await _fallback.DeleteAsync(key, ct);
    }
}

public sealed class SecretStoreUnavailableException(string message) : Exception(message);
