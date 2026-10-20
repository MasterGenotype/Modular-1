using Microsoft.Extensions.Logging;
using Modular.Core.Configuration;
using Modular.Core.Dependencies.Conventions;
using Modular.Core.Utilities;
using Modular.Sdk.Backends;

namespace Modular.Core.Services;

/// <summary>
/// Says which category a backend files a downloaded mod under.
/// </summary>
public interface ILibraryCategoryLookup
{
    /// <summary>
    /// Finds the category of a mod folder.
    /// </summary>
    /// <param name="backendId">The backend the mod was downloaded from.</param>
    /// <param name="modFolder">The mod folder's name, which is the mod's sanitized name.</param>
    /// <param name="recordedModId">The mod id a download record in the folder names, if any.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The category's name; <c>null</c> when it is not known.</returns>
    Task<string?> FindCategoryAsync(string backendId, string modFolder, string? recordedModId, CancellationToken ct);
}

/// <summary>
/// Answers from categories already known, by mod folder name, and asks another lookup
/// for the rest.
/// </summary>
/// <remarks>
/// A mod just downloaded came with its category in the listing it was picked from, which
/// covers mods found by search that are not among the user's subscriptions.
/// </remarks>
/// <param name="known">Categories by mod folder name.</param>
/// <param name="fallback">Asked for any folder not in <paramref name="known"/>.</param>
public sealed class KnownCategoryLookup(
    IReadOnlyDictionary<string, string> known,
    ILibraryCategoryLookup? fallback) : ILibraryCategoryLookup
{
    /// <inheritdoc />
    public Task<string?> FindCategoryAsync(
        string backendId, string modFolder, string? recordedModId, CancellationToken ct) =>
        known.TryGetValue(modFolder, out var category)
            ? Task.FromResult<string?>(category)
            : fallback?.FindCategoryAsync(backendId, modFolder, recordedModId, ct) ?? Task.FromResult<string?>(null);
}

/// <summary>
/// The outcome of sorting one game folder.
/// </summary>
/// <param name="Moved">How many mod folders moved.</param>
/// <param name="Uncategorized">Mods left where they were because their category is unknown.</param>
/// <param name="Errors">Moves that failed, as messages fit to show.</param>
public sealed record LibrarySortResult(
    int Moved,
    IReadOnlyList<string> Uncategorized,
    IReadOnlyList<string> Errors);

/// <summary>
/// Sorts a GameBanana or mod.io game folder into category folders,
/// <c>&lt;game&gt;/&lt;category&gt;/&lt;mod&gt;/</c>, or takes the mods back out of them.
/// </summary>
/// <remarks>
/// <para>
/// NexusMods game folders are sorted by <see cref="RenameService"/>, which also renames
/// their mod-id folders after the mod. GameBanana and mod.io folders are named after the
/// mod already, so they only need moving.
/// </para>
/// <para>
/// A folder holding files is a mod; a folder holding only folders is a category from an
/// earlier sort. A mod the site has no category for goes in <see cref="Uncategorized"/>;
/// one whose category could not be looked up at all stays where it is, so a run without
/// the network never undoes an earlier sort.
/// </para>
/// </remarks>
public sealed class ModLibrarySorter
{
    /// <summary>
    /// Category folder for mods the site files under no category, or no longer lists.
    /// </summary>
    public const string Uncategorized = "Uncategorized";

    private readonly ILibraryCategoryLookup? _categories;
    private readonly Func<string, string, CancellationToken, Task>? _folderMoved;
    private readonly ILogger? _logger;

    /// <summary>Creates the sorter.</summary>
    /// <param name="categories">Finds each mod's category; required to sort by category.</param>
    /// <param name="folderMoved">Called with the old and new path of every moved folder.</param>
    /// <param name="logger">Optional logger.</param>
    public ModLibrarySorter(
        ILibraryCategoryLookup? categories = null,
        Func<string, string, CancellationToken, Task>? folderMoved = null,
        ILogger? logger = null)
    {
        _categories = categories;
        _folderMoved = folderMoved;
        _logger = logger;
    }

    /// <summary>
    /// Sorts one game folder.
    /// </summary>
    /// <param name="backendId">The backend the folder belongs to.</param>
    /// <param name="gameFolder">The game folder.</param>
    /// <param name="byCategory">
    /// Whether mods go in category folders; when <c>false</c> they come out of them.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What moved, and what could not be sorted.</returns>
    public async Task<LibrarySortResult> SortAsync(
        string backendId, string gameFolder, bool byCategory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backendId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameFolder);

        var moved = 0;
        var uncategorized = new List<string>();
        var errors = new List<string>();

        if (!Directory.Exists(gameFolder))
            return new LibrarySortResult(0, uncategorized, errors);

        foreach (var mod in ModFolders(gameFolder))
        {
            ct.ThrowIfCancellationRequested();

            var name = Path.GetFileName(mod);
            string destination;

            if (byCategory)
            {
                var category = _categories == null
                    ? null
                    : await _categories.FindCategoryAsync(backendId, name, RecordedModId(mod), ct);

                if (string.IsNullOrWhiteSpace(category))
                {
                    uncategorized.Add(name);
                    continue;
                }

                var categoryFolder = Path.Combine(gameFolder, FileUtils.SanitizeDirectoryName(category.Trim()));

                // A mod already holds that name; a category folder cannot share it.
                if (HoldsFiles(categoryFolder))
                {
                    errors.Add($"'{name}' was not moved into '{category}': a mod folder has that name.");
                    continue;
                }

                destination = Path.Combine(categoryFolder, name);
            }
            else
            {
                destination = Path.Combine(gameFolder, name);
            }

            if (await MoveAsync(mod, destination, errors, ct))
                moved++;
        }

        // Category folders a run emptied, or that the other setting no longer uses.
        foreach (var folder in Directory.GetDirectories(gameFolder))
        {
            if (!Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }

        return new LibrarySortResult(moved, uncategorized, errors);
    }

    /// <summary>
    /// Sorts every game folder of one backend, with categories from its subscriptions.
    /// </summary>
    /// <param name="backend">The GameBanana or mod.io backend.</param>
    /// <param name="settings">Settings; <see cref="AppSettings.OrganizeByCategory"/> picks the layout.</param>
    /// <param name="modsDirectory">The mods directory.</param>
    /// <param name="folderMoved">Called with the old and new path of every moved folder.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many mod folders moved.</returns>
    public static async Task<int> SortBackendAsync(
        IModBackend backend,
        AppSettings settings,
        string modsDirectory,
        Func<string, string, CancellationToken, Task>? folderMoved = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(settings);

        var sorter = new ModLibrarySorter(new BackendLibraryGameLookup([backend], settings), folderMoved);
        var moved = 0;

        foreach (var game in ModLibraryLayout.ListGames(modsDirectory).Where(g => g.BackendId == backend.Id))
            moved += (await sorter.SortAsync(game.BackendId, game.Path, settings.OrganizeByCategory, ct)).Moved;

        return moved;
    }

    /// <summary>
    /// The mod folders of a game folder, whether loose in it or in a category folder.
    /// </summary>
    /// <param name="gameFolder">The game folder.</param>
    /// <returns>Mod folder paths.</returns>
    public static IReadOnlyList<string> ModFolders(string gameFolder)
    {
        var mods = new List<string>();

        foreach (var dir in Directory.GetDirectories(gameFolder).OrderBy(d => d, StringComparer.Ordinal))
        {
            if (Path.GetFileName(dir).StartsWith('.'))
                continue;

            if (HoldsFiles(dir))
            {
                mods.Add(dir);
                continue;
            }

            // A category folder: the mods are inside it.
            mods.AddRange(Directory.GetDirectories(dir)
                .Where(d => !Path.GetFileName(d).StartsWith('.'))
                .OrderBy(d => d, StringComparer.Ordinal));
        }

        return mods;
    }

    private static bool HoldsFiles(string folder) =>
        Directory.Exists(folder) &&
        Directory.EnumerateFiles(folder).Any(f => !Path.GetFileName(f).StartsWith('.'));

    // The mod id a download record in the folder names.
    private static string? RecordedModId(string modFolder)
    {
        try
        {
            return Directory
                .EnumerateFiles(modFolder, "*" + DependencyConventions.DownloadRecordSuffix)
                .Select(f => DependencyConventions.ReadDownloadRecord(
                    f[..^DependencyConventions.DownloadRecordSuffix.Length]).Source?.ModId)
                .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<bool> MoveAsync(string source, string destination, List<string> errors, CancellationToken ct)
    {
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.Ordinal))
            return false;

        try
        {
            var complete = FileUtils.MoveDirectory(source, destination);

            if (_folderMoved != null)
                await _folderMoved(source, destination, ct);

            if (!complete)
                errors.Add($"Could not move everything from {source} to {destination}.");

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
