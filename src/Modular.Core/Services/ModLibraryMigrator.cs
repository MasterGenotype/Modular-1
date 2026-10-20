using Microsoft.Extensions.Logging;
using Modular.Core.Backends.ModIo;
using Modular.Core.Configuration;
using Modular.Core.Dependencies.Conventions;
using Modular.Core.Utilities;
using Modular.Sdk.Backends;

namespace Modular.Core.Services;

/// <summary>
/// Says which game a mod in an old, game-less download folder is for.
/// </summary>
public interface ILibraryGameLookup
{
    /// <summary>
    /// Finds the game of a mod folder from a backend's old flat download folder.
    /// </summary>
    /// <param name="backendId">The backend the folder belongs to.</param>
    /// <param name="modFolder">The mod folder's name, which is the mod's sanitized name.</param>
    /// <param name="recordedGame">The game named by a download record in the folder, if any.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The game, as <see cref="ModLibraryLayout.GameFolder"/> takes it; <c>null</c> when unknown.</returns>
    Task<string?> FindGameAsync(string backendId, string modFolder, string? recordedGame, CancellationToken ct);
}

/// <summary>
/// The outcome of moving the mods directory into the backend/game layout.
/// </summary>
/// <param name="Moved">How many folders were moved.</param>
/// <param name="Unresolved">Mod folders left where they were, because their game is unknown.</param>
/// <param name="Errors">Moves that failed, as messages fit to show.</param>
public sealed record LibraryMigrationResult(
    int Moved,
    IReadOnlyList<string> Unresolved,
    IReadOnlyList<string> Errors);

/// <summary>
/// Moves downloads from the old mods-directory layouts into
/// <c>&lt;root&gt;/&lt;backend&gt;/&lt;game&gt;/&lt;mod&gt;</c>.
/// </summary>
/// <remarks>
/// <para>Two old layouts are recognised:</para>
/// <list type="bullet">
/// <item><description>
/// NexusMods game folders directly in the root, <c>&lt;root&gt;/&lt;domain&gt;/…</c>. Every
/// root folder that is not a backend folder, a dot-folder or an old flat folder is one of
/// these, and moves whole into <c>nexusmods/</c>.
/// </description></item>
/// <item><description>
/// Flat GameBanana and mod.io folders, <c>&lt;root&gt;/gamebanana/&lt;mod&gt;/</c>, named by the
/// <c>gamebanana_download_dir</c> and <c>modio_download_dir</c> settings. Their mod folders
/// hold archives directly, where a game folder holds only mod folders, which is how the two
/// are told apart when the old folder has the backend's name. A mod's game comes from
/// <see cref="ILibraryGameLookup"/>; a mod whose game is unknown is left where it is.
/// </description></item>
/// </list>
/// <para>
/// Installed mods keep the path of the archive they were installed from, so every move is
/// reported to a callback that repoints those records.
/// </para>
/// </remarks>
public sealed class ModLibraryMigrator
{
    private readonly ILibraryGameLookup? _games;
    private readonly Func<string, string, CancellationToken, Task>? _folderMoved;
    private readonly ILogger? _logger;

    /// <summary>Creates the migrator.</summary>
    /// <param name="games">Finds the game of a flat-folder mod; without it those mods stay put.</param>
    /// <param name="folderMoved">Called with the old and new path of every moved folder.</param>
    /// <param name="logger">Optional logger.</param>
    public ModLibraryMigrator(
        ILibraryGameLookup? games = null,
        Func<string, string, CancellationToken, Task>? folderMoved = null,
        ILogger? logger = null)
    {
        _games = games;
        _folderMoved = folderMoved;
        _logger = logger;
    }

    /// <summary>
    /// The old flat download folders, by folder name, with the backend each belongs to.
    /// </summary>
    /// <param name="settings">Settings naming the folders, or null for the defaults.</param>
    public static IReadOnlyDictionary<string, string> LegacyFlatFolders(AppSettings? settings)
    {
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BackendIds.GameBanana] = BackendIds.GameBanana,
            [BackendIds.ModIo] = BackendIds.ModIo
        };

        if (!string.IsNullOrWhiteSpace(settings?.GameBananaDownloadDir) &&
            !ModLibraryLayout.IsBackendFolder(settings.GameBananaDownloadDir))
        {
            folders[settings.GameBananaDownloadDir] = BackendIds.GameBanana;
        }

        if (!string.IsNullOrWhiteSpace(settings?.ModIoDownloadDir) &&
            !ModLibraryLayout.IsBackendFolder(settings.ModIoDownloadDir))
        {
            folders[settings.ModIoDownloadDir] = BackendIds.ModIo;
        }

        return folders;
    }

    /// <summary>
    /// Whether anything in the mods directory is still in an old layout.
    /// </summary>
    /// <param name="root">The mods directory.</param>
    /// <param name="legacyFlatFolders">From <see cref="LegacyFlatFolders"/>.</param>
    public static bool NeedsMigration(string? root, IReadOnlyDictionary<string, string> legacyFlatFolders)
    {
        ArgumentNullException.ThrowIfNull(legacyFlatFolders);

        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return false;

        try
        {
            return LegacyNexusFolders(root, legacyFlatFolders).Any() ||
                   legacyFlatFolders.Any(f => LegacyModFolders(root, f.Key).Any());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Moves everything in an old layout into the backend/game layout.
    /// </summary>
    /// <param name="root">The mods directory.</param>
    /// <param name="legacyFlatFolders">From <see cref="LegacyFlatFolders"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What was moved, and what could not be.</returns>
    public async Task<LibraryMigrationResult> MigrateAsync(
        string root,
        IReadOnlyDictionary<string, string> legacyFlatFolders,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(legacyFlatFolders);

        var moved = 0;
        var unresolved = new List<string>();
        var errors = new List<string>();

        if (!Directory.Exists(root))
            return new LibraryMigrationResult(0, unresolved, errors);

        // 1. NexusMods game folders sitting in the root.
        foreach (var folder in LegacyNexusFolders(root, legacyFlatFolders).ToList())
        {
            ct.ThrowIfCancellationRequested();

            var destination = ModLibraryLayout.GameFolder(root, BackendIds.NexusMods, Path.GetFileName(folder));
            if (await MoveAsync(folder, destination, errors, ct))
                moved++;
        }

        // 2. Flat GameBanana and mod.io folders, one mod folder at a time.
        foreach (var (folderName, backendId) in legacyFlatFolders)
        {
            foreach (var oldModFolder in LegacyModFolders(root, folderName).ToList())
            {
                foreach (var (modFolder, name) in ModsIn(oldModFolder, Path.GetFileName(oldModFolder)))
                {
                    ct.ThrowIfCancellationRequested();

                    var game = _games == null
                        ? null
                        : await _games.FindGameAsync(backendId, name, RecordedGame(modFolder), ct);

                    if (string.IsNullOrWhiteSpace(game))
                    {
                        unresolved.Add($"{folderName}/{name}");
                        continue;
                    }

                    var destination = Path.Combine(ModLibraryLayout.GameFolder(root, backendId, game), name);
                    if (await MoveAsync(modFolder, destination, errors, ct))
                        moved++;
                }

                // Folders a slash in a mod's name made are empty once the mod has moved.
                DeleteEmptyFolders(oldModFolder);
            }

            // An old folder under another name than the backend's is empty once its mods
            // have moved out.
            var oldFolder = Path.Combine(root, folderName);
            if (!ModLibraryLayout.IsBackendFolder(folderName) &&
                Directory.Exists(oldFolder) &&
                !Directory.EnumerateFileSystemEntries(oldFolder).Any())
            {
                Directory.Delete(oldFolder);
            }
        }

        return new LibraryMigrationResult(moved, unresolved, errors);
    }

    /// <summary>
    /// Root folders that are NexusMods game folders from before backend folders existed.
    /// </summary>
    /// <param name="root">The mods directory.</param>
    /// <param name="legacyFlatFolders">From <see cref="LegacyFlatFolders"/>.</param>
    public static IEnumerable<string> LegacyNexusFolders(
        string root, IReadOnlyDictionary<string, string> legacyFlatFolders) =>
        Directory.GetDirectories(root)
            .Where(d =>
            {
                var name = Path.GetFileName(d);
                return !name.StartsWith('.') &&
                       !ModLibraryLayout.IsBackendFolder(name) &&
                       !legacyFlatFolders.ContainsKey(name);
            })
            .OrderBy(d => d, StringComparer.Ordinal);

    /// <summary>
    /// The mod folders still in an old flat download folder.
    /// </summary>
    /// <remarks>
    /// Under the backend's own name the folder is also where game folders go; see
    /// <see cref="ModLibraryLayout.IsOldFlatModFolder"/> for telling them apart.
    /// </remarks>
    /// <param name="root">The mods directory.</param>
    /// <param name="folderName">The old folder's name, a key of <see cref="LegacyFlatFolders"/>.</param>
    public static IEnumerable<string> LegacyModFolders(string root, string folderName)
    {
        var folder = Path.Combine(root, folderName);
        if (!Directory.Exists(folder))
            return [];

        return Directory.GetDirectories(folder)
            .Where(d => !Path.GetFileName(d).StartsWith('.'))
            .Where(d => !ModLibraryLayout.IsBackendFolder(folderName) ||
                        ModLibraryLayout.IsOldFlatModFolder(folderName, d))
            .OrderBy(d => d, StringComparer.Ordinal);
    }

    /// <summary>
    /// The mods in an old flat mod folder, each with the folder name it should have.
    /// </summary>
    /// <param name="folder">The folder.</param>
    /// <param name="name">Its name, as the mod's name was saved.</param>
    /// <remarks>
    /// A folder holding files is one mod, moved whole. A folder holding only folders is
    /// what a slash in the mod's name made: "Body type 3/4 patch" was saved as
    /// <c>Body type 3/4 patch/</c>. Its mods are found further down and named the way the
    /// name is saved now, with the slash as an underscore: <c>Body type 3_4 patch</c>.
    /// </remarks>
    private static IEnumerable<(string Folder, string Name)> ModsIn(string folder, string name)
    {
        if (Directory.EnumerateFiles(folder).Any(f => !Path.GetFileName(f).StartsWith('.')))
        {
            yield return (folder, name);
            yield break;
        }

        foreach (var child in Directory.GetDirectories(folder).OrderBy(d => d, StringComparer.Ordinal))
        {
            foreach (var mod in ModsIn(child, $"{name}_{Path.GetFileName(child)}"))
                yield return mod;
        }
    }

    // Removes a folder and the folders beneath it if none of them holds a file.
    private static void DeleteEmptyFolders(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
                return;

            foreach (var child in Directory.GetDirectories(folder))
                DeleteEmptyFolders(child);

            if (!Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind; the next run tries again.
        }
    }

    // The game a download record in the folder names, for backends whose records carry one.
    private static string? RecordedGame(string modFolder)
    {
        try
        {
            return Directory
                .EnumerateFiles(modFolder, "*" + DependencyConventions.DownloadRecordSuffix)
                .Select(f => DependencyConventions.ReadDownloadRecord(
                    f[..^DependencyConventions.DownloadRecordSuffix.Length]).Source?.GameDomain)
                .FirstOrDefault(g => !string.IsNullOrWhiteSpace(g));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<bool> MoveAsync(string source, string destination, List<string> errors, CancellationToken ct)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);

        if (string.Equals(source, destination, StringComparison.Ordinal))
            return false;

        try
        {
            // A folder cannot move into itself: gamebanana/x moving to gamebanana/x/x goes
            // by way of a temporary name.
            var from = source;
            if (destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                from = source + ".modular-move";
                Directory.Move(source, from);
            }

            var complete = FileUtils.MoveDirectory(from, destination);

            if (_folderMoved != null)
                await _folderMoved(source, destination, ct);

            if (!complete)
                errors.Add($"Could not move everything from {source} to {destination}.");
            else
                _logger?.LogInformation("Moved {Source} to {Destination}", source, destination);

            return complete;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Could not move {Source} to {Destination}", source, destination);
            errors.Add($"Could not move {source} to {destination}: {ex.Message}");
            return false;
        }
    }
}

/// <summary>
/// Finds a flat-folder mod's game from the backends: the mod's download record, then the
/// user's subscriptions (the listing the folder was named from), then the only configured
/// game.
/// </summary>
public sealed class BackendLibraryGameLookup : ILibraryGameLookup
{
    private readonly IReadOnlyList<IModBackend> _backends;
    private readonly AppSettings _settings;
    private readonly ILogger? _logger;
    private readonly Dictionary<string, Dictionary<string, string>> _subscriptions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the lookup.</summary>
    /// <param name="backends">The enabled backends.</param>
    /// <param name="settings">Settings naming the configured games.</param>
    /// <param name="logger">Optional logger.</param>
    public BackendLibraryGameLookup(IEnumerable<IModBackend> backends, AppSettings settings, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(backends);
        ArgumentNullException.ThrowIfNull(settings);

        _backends = backends.ToList();
        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string?> FindGameAsync(
        string backendId, string modFolder, string? recordedGame, CancellationToken ct)
    {
        var game = recordedGame;

        if (string.IsNullOrWhiteSpace(game))
        {
            var subscriptions = await SubscriptionsAsync(backendId, ct);
            subscriptions.TryGetValue(modFolder, out game);
        }

        if (string.IsNullOrWhiteSpace(game) &&
            string.Equals(backendId, BackendIds.ModIo, StringComparison.OrdinalIgnoreCase) &&
            _settings.ModIoGameIds.Count == 1)
        {
            game = _settings.ModIoGameIds[0].ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (string.IsNullOrWhiteSpace(game))
            return null;

        // mod.io knows games by number; the folder is named after the game's name.
        return _backends.OfType<ModIoBackend>().FirstOrDefault() is { } modIo &&
               string.Equals(backendId, BackendIds.ModIo, StringComparison.OrdinalIgnoreCase)
            ? await modIo.GetGameNameAsync(game, ct)
            : game;
    }

    // Each subscribed mod's game, by the folder name a download of it was given.
    private async Task<Dictionary<string, string>> SubscriptionsAsync(string backendId, CancellationToken ct)
    {
        if (_subscriptions.TryGetValue(backendId, out var known))
            return known;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var backend = _backends.FirstOrDefault(b => string.Equals(b.Id, backendId, StringComparison.OrdinalIgnoreCase));

        if (backend != null)
        {
            try
            {
                foreach (var mod in await backend.GetUserModsAsync(null, ct))
                {
                    if (!string.IsNullOrWhiteSpace(mod.Name) && !string.IsNullOrWhiteSpace(mod.GameDomain))
                        map.TryAdd(FileUtils.SanitizeDirectoryName(mod.Name), mod.GameDomain);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Could not list {Backend} subscriptions to sort old downloads", backendId);
            }
        }

        return _subscriptions[backendId] = map;
    }
}
