using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Modular.Core.Database;
using Modular.Core.Modules;
using Modular.Sdk.Modules;

namespace Modular.Core.GameDetection;

/// <summary>
/// Turns whatever the user typed after <c>--game</c> into a fully resolved
/// <see cref="GameInstallation"/>.
/// </summary>
/// <remarks>
/// <para>
/// Accepts a Steam AppID, a NexusMods slug, a GameBanana game id, a display name
/// or a filesystem path. Lookup tries the Steam scan first — exact AppID, then a
/// module's declared game ids, then a name match — and falls back to treating the
/// input as a path.
/// </para>
/// <para>
/// For paths, the resolver walks up to three parents looking for a directory a
/// module anchors on, so pointing at <c>bin/x64</c> or <c>End/Binaries/Win64</c>
/// still resolves the real game root.
/// </para>
/// <para>
/// Every successful resolution is written back to <c>detected_games</c> and
/// <c>engine_detection</c>, which is what finally makes auto-snapshots fire.
/// </para>
/// </remarks>
public sealed class GameInstallationResolver
{
    /// <summary>How many parent directories a path lookup will walk up.</summary>
    public const int MaxWalkUpDepth = 3;

    private readonly GameModuleRegistry _registry;
    private readonly SteamGameScanner _steamScanner;
    private readonly CompositeEngineDetector _engineDetector;
    private readonly ModularDatabase? _database;
    private readonly ILogger<GameInstallationResolver>? _logger;

    /// <summary>
    /// Creates a resolver.
    /// </summary>
    /// <param name="registry">Module registry used for detection and id lookup.</param>
    /// <param name="steamScanner">Steam scanner; a default one is created when omitted.</param>
    /// <param name="engineDetector">Engine detector; a default one is created when omitted.</param>
    /// <param name="database">Database for detection upserts, or <c>null</c> to skip persistence.</param>
    /// <param name="logger">Optional logger.</param>
    public GameInstallationResolver(
        GameModuleRegistry? registry = null,
        SteamGameScanner? steamScanner = null,
        CompositeEngineDetector? engineDetector = null,
        ModularDatabase? database = null,
        ILogger<GameInstallationResolver>? logger = null)
    {
        _registry = registry ?? new GameModuleRegistry();
        _steamScanner = steamScanner ?? new SteamGameScanner();
        _engineDetector = engineDetector ?? new CompositeEngineDetector();
        _database = database;
        _logger = logger;
    }

    /// <summary>
    /// Resolves a user-supplied game reference.
    /// </summary>
    /// <param name="gameRef">AppID, slug, GameBanana id, display name or path.</param>
    /// <param name="persist">Whether to upsert the result into the detection tables.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resolved installation, or <c>null</c> when nothing matched.</returns>
    public async Task<GameInstallation?> ResolveAsync(
        string gameRef,
        bool persist = true,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gameRef))
            return null;

        var installation =
            await ResolveFromSteamAsync(gameRef, ct) ??
            await ResolveFromPathAsync(gameRef, ct);

        if (installation == null)
        {
            _logger?.LogWarning("Could not resolve game reference '{GameRef}'", gameRef);
            return null;
        }

        if (persist)
            await PersistAsync(installation, ct);

        return installation;
    }

    /// <summary>
    /// Resolves a game that is already known to be a Steam installation.
    /// </summary>
    /// <param name="steamGame">The scanned Steam game.</param>
    /// <returns>The resolved installation.</returns>
    public GameInstallation ResolveFromSteamGame(SteamGameInstall steamGame)
    {
        ArgumentNullException.ThrowIfNull(steamGame);

        var module = _registry.ResolveByAppId(steamGame.AppId);

        // A module may still claim the directory even when it does not list the
        // AppID, which is how regional or non-Steam re-releases get picked up.
        if (module == null && Directory.Exists(steamGame.InstallPath))
            module = _registry.ResolveByPath(steamGame.InstallPath)?.Module;

        var engine = SafeDetectEngine(steamGame.InstallPath);

        return new GameInstallation
        {
            GameId = PreferredGameId(module, steamGame.AppId),
            SteamAppId = steamGame.AppId,
            InstallPath = steamGame.InstallPath,
            Store = GameStore.Steam,
            ProtonPrefixPath = steamGame.ProtonPrefixPath,
            EngineFamily = engine?.EngineFamily,
            EngineConfidence = engine?.Confidence,
            ModuleId = module?.ModuleId ?? GenericGameModule.Id,
            BuildId = steamGame.BuildId,
            DisplayName = steamGame.DisplayName,
            LibraryRoot = steamGame.LibraryRoot
        };
    }

    /// <summary>
    /// Resolves the module that owns an already-resolved installation.
    /// </summary>
    /// <param name="installation">The installation.</param>
    /// <returns>The owning module; the generic fallback when none claims it.</returns>
    public IGameModule GetModule(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);

        return _registry.GetByModuleId(installation.ModuleId)
            ?? _registry.Resolve(
                installation.GameId, installation.SteamAppId, installation.InstallPath);
    }

    private async Task<GameInstallation?> ResolveFromSteamAsync(
        string gameRef, CancellationToken ct)
    {
        List<SteamGameInstall> games;
        try
        {
            games = await _steamScanner.ScanAllAsync(ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Steam scan failed; falling back to path resolution");
            return null;
        }

        if (games.Count == 0)
            return null;

        // 1. Exact AppID.
        if (int.TryParse(gameRef, NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId))
        {
            var byAppId = games.FirstOrDefault(g => g.AppId == appId);
            if (byAppId != null)
                return ResolveFromSteamGame(byAppId);
        }

        // 2. A module's declared game ids, mapped back to the AppIDs it claims.
        var module = _registry.ResolveByGameId(gameRef);
        if (module != null)
        {
            var byModule = games.FirstOrDefault(g => module.SteamAppIds.Contains(g.AppId));
            if (byModule != null)
                return ResolveFromSteamGame(byModule);
        }

        // 3. Display name: exact first, then a unique substring match. An
        //    ambiguous substring is rejected rather than guessed at.
        var byExactName = games.FirstOrDefault(g =>
            string.Equals(g.DisplayName, gameRef, StringComparison.OrdinalIgnoreCase));
        if (byExactName != null)
            return ResolveFromSteamGame(byExactName);

        var byPartialName = games
            .Where(g => g.DisplayName.Contains(gameRef, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (byPartialName.Count == 1)
            return ResolveFromSteamGame(byPartialName[0]);

        if (byPartialName.Count > 1)
        {
            _logger?.LogWarning(
                "'{GameRef}' matches {Count} Steam games; be more specific: {Names}",
                gameRef, byPartialName.Count,
                string.Join(", ", byPartialName.Select(g => g.DisplayName)));
        }

        return null;
    }

    private async Task<GameInstallation?> ResolveFromPathAsync(string path, CancellationToken ct)
    {
        if (!Directory.Exists(path))
            return null;

        var (root, resolution) = WalkUpToGameRoot(path);

        var module = resolution?.Module;
        var engine = SafeDetectEngine(root);

        // A directory reached by path may still sit inside a Steam library, in
        // which case the AppID, build id and Proton prefix are all recoverable.
        var steamGame = await TryMatchSteamLibraryAsync(root, ct);

        return new GameInstallation
        {
            GameId = PreferredGameId(module, steamGame?.AppId),
            SteamAppId = steamGame?.AppId,
            InstallPath = root,
            Store = steamGame != null ? GameStore.Steam : GameStore.Manual,
            ProtonPrefixPath = steamGame?.ProtonPrefixPath,
            EngineFamily = engine?.EngineFamily,
            EngineConfidence = engine?.Confidence,
            ModuleId = module?.ModuleId ?? GenericGameModule.Id,
            BuildId = steamGame?.BuildId,
            DisplayName = steamGame?.DisplayName ?? new DirectoryInfo(root).Name,
            LibraryRoot = steamGame?.LibraryRoot
        };
    }

    /// <summary>
    /// Walks up from a directory looking for one a specific module anchors on.
    /// </summary>
    /// <param name="startPath">Where to start.</param>
    /// <returns>
    /// The best game root found and the module resolution for it. Falls back to
    /// the starting directory when no module claims anything on the way up.
    /// </returns>
    /// <remarks>
    /// Pointing at <c>&lt;game&gt;/bin/x64</c> or <c>&lt;game&gt;/End/Binaries/Win64</c>
    /// is a common mistake, and both are within the walk-up budget.
    /// </remarks>
    public (string Root, GameModuleResolution? Resolution) WalkUpToGameRoot(string startPath)
    {
        var current = new DirectoryInfo(startPath);

        for (var depth = 0; depth <= MaxWalkUpDepth && current != null; depth++)
        {
            var resolution = _registry.ResolveByPath(current.FullName);

            // The generic fallback claims every directory, so it is not evidence
            // that we have found the real game root. Keep walking.
            if (resolution != null &&
                !string.Equals(
                    resolution.Module.ModuleId, GenericGameModule.Id, StringComparison.Ordinal))
            {
                return (current.FullName, resolution);
            }

            current = current.Parent;
        }

        return (startPath, _registry.ResolveByPath(startPath));
    }

    private async Task<SteamGameInstall?> TryMatchSteamLibraryAsync(
        string installPath, CancellationToken ct)
    {
        try
        {
            var full = Path.GetFullPath(installPath).TrimEnd(Path.DirectorySeparatorChar);
            var games = await _steamScanner.ScanAllAsync(ct);

            return games.FirstOrDefault(g => string.Equals(
                Path.GetFullPath(g.InstallPath).TrimEnd(Path.DirectorySeparatorChar),
                full,
                StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Could not match {Path} against Steam libraries", installPath);
            return null;
        }
    }

    private EngineDetectionResult? SafeDetectEngine(string installPath)
    {
        if (!Directory.Exists(installPath))
            return null;

        try
        {
            return _engineDetector.Detect(installPath);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Engine detection failed for {Path}", installPath);
            return null;
        }
    }

    private static string PreferredGameId(IGameModule? module, int? appId)
    {
        // Prefer the module's first declared id, which is the NexusMods slug.
        var slug = module?.GameIds.FirstOrDefault();
        if (!string.IsNullOrEmpty(slug))
            return slug;

        return appId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>
    /// Upserts a resolved installation into <c>detected_games</c> and, when an
    /// engine was identified, <c>engine_detection</c>.
    /// </summary>
    /// <param name="installation">The installation to record.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the rows have been written.</returns>
    public async Task PersistAsync(GameInstallation installation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(installation);

        if (_database == null)
            return;

        // Both detection tables are keyed by (steam_appid, library_root), so a
        // non-Steam installation has nothing stable to key on.
        if (installation.SteamAppId is not { } appId)
            return;

        var libraryRoot = installation.LibraryRoot ?? installation.InstallPath;
        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        try
        {
            var connection = await _database.GetConnectionAsync();

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO detected_games
                        (steam_appid, display_name, install_path, library_root,
                         size_bytes, first_seen_utc, last_seen_utc)
                    VALUES ($appid, $name, $path, $library, $size, $now, $now)
                    ON CONFLICT(steam_appid, library_root) DO UPDATE SET
                        display_name  = excluded.display_name,
                        install_path  = excluded.install_path,
                        last_seen_utc = excluded.last_seen_utc;
                    """;
                cmd.Parameters.AddWithValue("$appid", appId);
                cmd.Parameters.AddWithValue(
                    "$name", (object?)installation.DisplayName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$path", installation.InstallPath);
                cmd.Parameters.AddWithValue("$library", libraryRoot);
                cmd.Parameters.AddWithValue("$size", 0L);
                cmd.Parameters.AddWithValue("$now", now);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            if (!string.IsNullOrEmpty(installation.EngineFamily))
            {
                await using var engineCmd = connection.CreateCommand();
                engineCmd.CommandText = """
                    INSERT INTO engine_detection
                        (steam_appid, library_root, engine_family, confidence,
                         evidence_json, detector_name, detected_at_utc)
                    VALUES ($appid, $library, $engine, $confidence,
                            $evidence, $detector, $now)
                    ON CONFLICT(steam_appid, library_root) DO UPDATE SET
                        engine_family   = excluded.engine_family,
                        confidence      = excluded.confidence,
                        evidence_json   = excluded.evidence_json,
                        detector_name   = excluded.detector_name,
                        detected_at_utc = excluded.detected_at_utc;
                    """;
                engineCmd.Parameters.AddWithValue("$appid", appId);
                engineCmd.Parameters.AddWithValue("$library", libraryRoot);
                engineCmd.Parameters.AddWithValue("$engine", installation.EngineFamily);
                engineCmd.Parameters.AddWithValue("$confidence", installation.EngineConfidence ?? 0.0);
                engineCmd.Parameters.AddWithValue("$evidence", DBNull.Value);
                engineCmd.Parameters.AddWithValue(
                    "$detector", installation.ModuleId ?? GenericGameModule.Id);
                engineCmd.Parameters.AddWithValue("$now", now);
                await engineCmd.ExecuteNonQueryAsync(ct);
            }

            _logger?.LogDebug(
                "Recorded detection for {Game} (AppID {AppId})",
                installation.DisplayName ?? installation.GameId, appId);
        }
        catch (SqliteException ex)
        {
            // Persistence is a side effect of resolution, never its purpose.
            _logger?.LogWarning(ex, "Failed to persist game detection for AppID {AppId}", appId);
        }
    }
}
