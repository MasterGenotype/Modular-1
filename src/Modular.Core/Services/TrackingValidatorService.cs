using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Modular.Core.Backends.NexusMods;
using Modular.Core.Configuration;
using Modular.Core.Models;

namespace Modular.Core.Services;

/// <summary>
/// Service for validating API tracking against web tracking center.
/// </summary>
public partial class TrackingValidatorService
{
    private readonly AppSettings _settings;
    private readonly NexusModsBackend _nexusBackend;
    private readonly ILogger<TrackingValidatorService>? _logger;

    [GeneratedRegex(@"/mods/(\d+)", RegexOptions.Compiled)]
    private static partial Regex ModIdRegex();

    public TrackingValidatorService(AppSettings settings, NexusModsBackend nexusBackend, ILogger<TrackingValidatorService>? logger = null)
    {
        _settings = settings;
        _nexusBackend = nexusBackend;
        _logger = logger;
    }

    /// <summary>
    /// Validates tracking for a specific game domain.
    /// </summary>
    public async Task<ValidationResult> ValidateAsync(string gameDomain, CancellationToken ct = default)
    {
        var result = new ValidationResult { GameDomain = gameDomain };

        // Get mods from API via backend
        var userMods = await _nexusBackend.GetUserModsAsync(gameDomain, ct);
        result.ApiMods = userMods.Select(m => int.Parse(m.ModId)).ToHashSet();

        // Get mods from web (would require web scraping with cookies)
        // For now, return API-only results
        _logger?.LogWarning("Web tracking validation not implemented - returning API mods only");

        return result;
    }

    /// <summary>
    /// Logs validation results.
    /// </summary>
    public void LogValidationResult(ValidationResult result)
    {
        if (!result.HasMismatch)
        {
            _logger?.LogInformation("Tracking validation passed for {Domain}: {Count} mods matched",
                result.GameDomain, result.MatchedMods.Count);
            return;
        }

        _logger?.LogWarning("Tracking validation mismatch detected for {Domain}!", result.GameDomain);
        _logger?.LogWarning("API mods: {ApiCount}, Web mods: {WebCount}, Matched: {MatchedCount}",
            result.ApiMods.Count, result.WebMods.Count, result.MatchedMods.Count);

        if (result.ApiOnlyMods.Count > 0)
        {
            _logger?.LogWarning("Mods only in API ({Count}):", result.ApiOnlyMods.Count);
            foreach (var modId in result.ApiOnlyMods.Take(20))
            {
                _logger?.LogWarning("  - Mod ID: {ModId}, Domain: {Domain}, URL: https://www.nexusmods.com/{Domain2}/mods/{ModId2}, Source: API",
                    modId, result.GameDomain, result.GameDomain, modId);
            }
        }

        if (result.WebOnlyMods.Count > 0)
        {
            _logger?.LogWarning("Mods only in Web ({Count}):", result.WebOnlyMods.Count);
            foreach (var modId in result.WebOnlyMods.Take(20))
            {
                _logger?.LogWarning("  - Mod ID: {ModId}, Domain: {Domain}, URL: https://www.nexusmods.com/{Domain2}/mods/{ModId2}, Source: Web",
                    modId, result.GameDomain, result.GameDomain, modId);
            }
        }
    }

    /// <summary>
    /// Checks if a mod is tracked (logs warning if not).
    /// </summary>
    public async Task<bool> IsModTrackedWithWarningAsync(string gameDomain, int modId, CancellationToken ct = default)
    {
        var isTracked = await _nexusBackend.IsModTrackedAsync(gameDomain, modId, ct);
        if (!isTracked)
        {
            _logger?.LogWarning("Mod {ModId} is NOT in tracked list. Skipping.", modId);
        }
        return isTracked;
    }
}
