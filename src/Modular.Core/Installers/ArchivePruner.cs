using Modular.Core.Database;
using Modular.Core.Dependencies.Conventions;
using Modular.Core.Deployment;
using Modular.Core.Downloads;
using Modular.Core.Utilities;
using Modular.Sdk.Archives;

namespace Modular.Core.Installers;

/// <summary>What <see cref="ArchivePruner"/> does, or would do, with one archive.</summary>
public enum ArchivePruneAction
{
    /// <summary>The archive of an installed, staged mod: replaced by its record.</summary>
    Release,

    /// <summary>An archive no installed mod came from: deleted with its records.</summary>
    Remove
}

/// <summary>One archive <see cref="ArchivePruner"/> acts on.</summary>
/// <param name="Path">The archive, or for a released one the path it had.</param>
/// <param name="Bytes">Its size; 0 for a record left from an earlier release.</param>
/// <param name="Action">What happens to it.</param>
/// <param name="ModId">The installed mod it belongs to, for a release.</param>
public sealed record ArchivePruneItem(string Path, long Bytes, ArchivePruneAction Action, string? ModId = null);

/// <summary>Outcome of a prune.</summary>
public sealed class ArchivePruneResult
{
    /// <summary>Every archive acted on, or that would be in a dry run.</summary>
    public List<ArchivePruneItem> Items { get; } = [];

    /// <summary>Archives that could not be released or removed, with why.</summary>
    public List<string> Errors { get; } = [];

    /// <summary>Bytes freed; in a dry run, the bytes that would be.</summary>
    public long BytesFreed { get; set; }

    /// <summary>Installed mods whose staged files are missing; their archives are left alone.</summary>
    public List<string> Unstaged { get; } = [];
}

/// <summary>
/// Brings a game's archives down to one copy of each installed mod, for archives that
/// predate <see cref="ArchiveRelease"/> or were kept with
/// <see cref="Configuration.AppSettings.KeepInstalledArchives"/>.
/// </summary>
/// <remarks>
/// <para>
/// The archives of the game's installed mods are released: their staged files are the
/// mod's copy. A mod whose staging is missing keeps its archive, which is then its only copy.
/// </para>
/// <para>
/// With <c>removeUnused</c>, every other archive of the game in Modular's folders is
/// deleted along with its records: older versions of installed mods, mods since
/// uninstalled, and downloads never installed. The game's folders are its folder in the
/// archive store and the library folders (<c>&lt;backend&gt;/&lt;game&gt;</c>) that any of its
/// changesets ever installed from. Readmes and pictures saved beside downloads are kept.
/// </para>
/// </remarks>
public sealed class ArchivePruner
{
    private readonly ModularDatabase _database;
    private readonly IArchiveReaderFactory _archiveReaderFactory;

    /// <summary>Creates the pruner.</summary>
    /// <param name="database">Modular's database.</param>
    /// <param name="archiveReaderFactory">Opens archives to read what they declare.</param>
    public ArchivePruner(ModularDatabase database, IArchiveReaderFactory? archiveReaderFactory = null)
    {
        _database = database;
        _archiveReaderFactory = archiveReaderFactory ?? new Archives.ArchiveReaderFactory();
    }

    /// <summary>Prunes one game's archives.</summary>
    /// <param name="gameKey">The game, normally its Steam AppID.</param>
    /// <param name="modsDirectory">The mods directory, which holds the library and the archive store.</param>
    /// <param name="archiveStorePath">The archive store; defaults to <c>.archives</c> in the mods directory.</param>
    /// <param name="removeUnused">Also delete archives no installed mod came from.</param>
    /// <param name="apply">Act; otherwise only report what would be done.</param>
    /// <param name="progress">Reports each archive as it is handled.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What was done, or would be.</returns>
    public async Task<ArchivePruneResult> PruneAsync(
        string gameKey,
        string modsDirectory,
        string? archiveStorePath,
        bool removeUnused,
        bool apply,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var result = new ArchivePruneResult();
        var modsRoot = Path.GetFullPath(FileUtils.ExpandPath(modsDirectory));
        var store = new ArchiveStore(
            archiveStorePath ?? Path.Combine(modsRoot, ArchiveStore.FolderName), [modsRoot]);
        var changesets = new ChangesetManager(_database);

        // The archives the installed mods came from. A mod without staged files has no
        // other copy, so its archive is kept, and counts as in use.
        var inUse = new HashSet<string>(PathComparer);
        var toRelease = new List<(string Path, string ModId)>();
        foreach (var mod in await new GameModRepository(_database).ListAsync(gameKey, enabledOnly: false, ct))
        {
            var archive = mod.ChangesetId == null
                ? null
                : (await changesets.GetChangesetAsync(mod.ChangesetId, ct))?.ArchivePath;
            if (string.IsNullOrEmpty(archive))
                continue;

            var full = Path.GetFullPath(archive);
            inUse.Add(full);

            if (!HasStagedFiles(mod.StagingPath))
            {
                result.Unstaged.Add(mod.ModId);
                continue;
            }

            if (File.Exists(full) && store.IsManaged(full))
                toRelease.Add((full, mod.ModId));
        }

        foreach (var (path, modId) in toRelease)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = new FileInfo(path).Length;
            result.Items.Add(new ArchivePruneItem(path, bytes, ArchivePruneAction.Release, modId));
            if (!apply)
            {
                result.BytesFreed += bytes;
                continue;
            }

            progress?.Report($"Releasing {Path.GetFileName(path)}");
            try
            {
                result.BytesFreed += ArchiveRelease.Release(path, _archiveReaderFactory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Errors.Add($"{path}: {ex.Message}");
            }
        }

        if (!removeUnused)
            return result;

        foreach (var folder in await GameFoldersAsync(gameKey, modsRoot, store, ct))
        {
            foreach (var path in UnusedArchives(folder, inUse))
            {
                ct.ThrowIfCancellationRequested();
                var bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
                result.Items.Add(new ArchivePruneItem(path, bytes, ArchivePruneAction.Remove));
                result.BytesFreed += bytes;
                if (!apply)
                    continue;

                progress?.Report($"Removing {Path.GetFileName(path)}");
                try
                {
                    foreach (var file in new[]
                             {
                                 path,
                                 path + DependencyConventions.DownloadRecordSuffix,
                                 path + DependencyConventions.ModOrganizerSuffix
                             })
                    {
                        if (File.Exists(file))
                            File.Delete(file);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result.Errors.Add($"{path}: {ex.Message}");
                }
            }

            if (apply)
                RemoveEmptyFolders(folder);
        }

        return result;
    }

    // The game's folder in the store, and each library game folder its changesets used.
    private async Task<IReadOnlyList<string>> GameFoldersAsync(
        string gameKey, string modsRoot, ArchiveStore store, CancellationToken ct)
    {
        var folders = new HashSet<string>(PathComparer)
        {
            Path.Combine(store.StorePath, gameKey)
        };

        var connection = await _database.GetConnectionAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT archive_path FROM changeset WHERE game_key = $game AND archive_path IS NOT NULL";
        cmd.Parameters.AddWithValue("$game", gameKey);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var relative = Path.GetRelativePath(modsRoot, Path.GetFullPath(reader.GetString(0)));
            var parts = relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && !relative.StartsWith("..", StringComparison.Ordinal) &&
                ModLibraryLayout.IsBackendFolder(parts[0]))
            {
                folders.Add(Path.Combine(modsRoot, parts[0], parts[1]));
            }
        }

        return folders.Where(Directory.Exists).OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    // Downloads, and records of released downloads, under a folder that no installed mod uses.
    private static IEnumerable<string> UnusedArchives(string folder, IReadOnlySet<string> inUse)
    {
        var candidates = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var directory in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).Prepend(folder))
        {
            foreach (var download in ModLibraryFiles.Downloads(directory))
                candidates.Add(Path.GetFullPath(download));
            foreach (var released in ModLibraryFiles.Released(directory))
                candidates.Add(Path.GetFullPath(released));
        }

        return candidates.Where(p => !inUse.Contains(p));
    }

    private static void RemoveEmptyFolders(string folder)
    {
        foreach (var directory in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
            catch (IOException)
            {
            }
        }
    }

    private static bool HasStagedFiles(string stagingPath) =>
        Directory.Exists(stagingPath) &&
        Directory.EnumerateFiles(stagingPath, "*", SearchOption.AllDirectories).Any();

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
