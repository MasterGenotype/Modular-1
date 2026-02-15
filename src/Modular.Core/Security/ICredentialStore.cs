namespace Modular.Core.Security;

/// <summary>
/// Interface for secure credential storage.
/// Implementations should use OS-level credential managers where available.
/// </summary>
/// <remarks>
/// Implementations may include:
/// - Windows: Credential Manager (Windows.Security.Credentials.PasswordVault)
/// - macOS: Keychain (Security framework)
/// - Linux: libsecret (GNOME Keyring / KDE Wallet)
/// - Fallback: Encrypted config file (for headless/container environments)
/// </remarks>
public interface ICredentialStore
{
    /// <summary>
    /// Stores a credential securely.
    /// </summary>
    /// <param name="key">Identifier for the credential (e.g., "nexusmods_api_key")</param>
    /// <param name="value">The secret value to store</param>
    /// <param name="ct">Cancellation token</param>
    Task SetCredentialAsync(string key, string value, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a credential.
    /// </summary>
    /// <param name="key">Identifier for the credential</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The secret value, or null if not found</returns>
    Task<string?> GetCredentialAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Removes a credential.
    /// </summary>
    /// <param name="key">Identifier for the credential</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if removed, false if not found</returns>
    Task<bool> RemoveCredentialAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Checks if a credential exists.
    /// </summary>
    /// <param name="key">Identifier for the credential</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if exists</returns>
    Task<bool> HasCredentialAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Whether this store uses secure OS-level storage.
    /// Implementations using plaintext config files should return false.
    /// </summary>
    bool IsSecure { get; }
}

/// <summary>
/// Standard credential keys used by Modular.
/// </summary>
public static class CredentialKeys
{
    public const string NexusModsApiKey = "nexusmods_api_key";
    public const string GameBananaUserId = "gamebanana_user_id";
}
