using Modular.Core.Configuration;

namespace Modular.Core.Authentication;

/// <summary>
/// Gets Modular a NexusMods API key through the browser SSO login and saves it, so
/// nobody has to find and paste a key by hand.
/// </summary>
/// <remarks>
/// One instance per process, shared: downloads running side by side that all find no
/// key wait for a single login instead of each opening the browser.
/// </remarks>
public sealed class NexusLogin
{
    private readonly AppSettings _settings;
    private readonly ConfigurationService _config;
    private readonly NexusSsoClient _sso;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public NexusLogin(AppSettings settings, ConfigurationService config, NexusSsoClient sso)
    {
        _settings = settings;
        _config = config;
        _sso = sso;
    }

    /// <summary>Whether an API key is configured, from SSO or otherwise.</summary>
    public bool HasKey => !string.IsNullOrWhiteSpace(_settings.NexusApiKey);

    /// <summary>
    /// Logs in through the browser and saves the key it gives, replacing any other key.
    /// </summary>
    /// <param name="onAuthorizationUrl">Receives the page to open, in case the browser doesn't.</param>
    /// <returns>The new key.</returns>
    public async Task<string> LoginAsync(Action<string>? onAuthorizationUrl = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await LoginLockedAsync(onAuthorizationUrl, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Logs in through the browser when there is no key yet and SSO is enabled.
    /// </summary>
    /// <returns>Whether a key is available afterwards.</returns>
    public async Task<bool> EnsureKeyAsync(Action<string>? onAuthorizationUrl = null, CancellationToken ct = default)
    {
        if (HasKey)
            return true;
        if (!_settings.NexusSsoEnabled)
            return false;

        await _gate.WaitAsync(ct);
        try
        {
            // Another caller may have logged in while this one waited.
            if (!HasKey)
                await LoginLockedAsync(onAuthorizationUrl, ct);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> LoginLockedAsync(Action<string>? onAuthorizationUrl, CancellationToken ct)
    {
        var key = await _sso.AuthenticateAsync(onAuthorizationUrl, ct);
        _settings.NexusApiKey = key;
        _settings.NexusApiKeyFromSso = true;
        await _config.SaveAsync(_settings);
        return key;
    }
}
