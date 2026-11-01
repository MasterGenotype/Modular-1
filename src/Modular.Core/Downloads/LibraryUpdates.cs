using Modular.Core.Dependencies.Conventions;
using Modular.Core.Utilities;
using Modular.Sdk.Backends;
using Modular.Sdk.Backends.Common;

namespace Modular.Core.Downloads;

/// <summary>
/// A mod in the mods directory, as its downloaded files identify it.
/// </summary>
public sealed class LibraryMod
{
    /// <summary>The mod's folder.</summary>
    public required string Folder { get; init; }

    /// <summary>The folder's name, which is how the library shows the mod.</summary>
    public required string Name { get; init; }

    /// <summary>The backend the files came from.</summary>
    public required string BackendId { get; init; }

    /// <summary>The mod's id on that backend.</summary>
    public required string ModId { get; init; }

    /// <summary>The game as the backend names it (a domain, or a mod.io game id), or null.</summary>
    public string? Game { get; init; }

    /// <summary>The category folder the mod sits in, or null.</summary>
    public string? Category { get; init; }

    /// <summary>Backend file ids of the files downloaded, where recorded.</summary>
    public IReadOnlySet<string> FileIds { get; init; } = new HashSet<string>();

    /// <summary>
    /// The newest of the downloads: its upload time where the file name carries one,
    /// else when it was written. Nothing uploaded before it can be an update.
    /// </summary>
    public DateTimeOffset? NewestDownload { get; init; }
}

/// <summary>What a backend offers a library mod that its folder does not have.</summary>
public sealed class LibraryModUpdate
{
    /// <summary>The mod.</summary>
    public required LibraryMod Mod { get; init; }

    /// <summary>Files uploaded after the mod's downloads, newest first.</summary>
    public IReadOnlyList<BackendModFile> NewFiles { get; init; } = [];

    /// <summary>
    /// The new files that succeed a download: for each file the folder has, the newest
    /// file of the same category and, where the backend names them, the same name.
    /// </summary>
    public IReadOnlyList<BackendModFile> Suggested { get; init; } = [];

    /// <summary>Why the backend could not be asked, or null.</summary>
    public string? Error { get; init; }

    /// <summary>Whether anything newer was found.</summary>
    public bool HasUpdate => NewFiles.Count > 0;
}

/// <summary>
/// Finds which mods in the mods directory have newer files on their backend.
/// </summary>
/// <remarks>
/// <para>
/// A mod folder is identified by its downloads: Modular's <c>.modular.json</c> record,
/// then Mod Organizer's <c>.meta</c>, then a NexusMods file name. The record also names the
/// file, which lets a newer file be told from one already present; without it, the
/// download's own time is the bar.
/// </para>
/// <para>
/// "Newer" is uploaded after the newest download. Of the newer files, the ones in the same
/// category as a download (main, optional) and of the same name are suggested, since a
/// mod page commonly offers several main files as variants and only the one in use wants
/// replacing.
/// </para>
/// </remarks>
public static class LibraryUpdates
{
    /// <summary>
    /// Identifies the mods in <paramref name="modFolders"/>.
    /// </summary>
    /// <param name="modsRoot">The mods directory, for the backend and game a folder sits under.</param>
    /// <param name="modFolders">Mod folders to read.</param>
    /// <param name="unidentified">Folders none of whose files say where they came from.</param>
    /// <returns>One entry per mod; a folder holding two mods' files yields two.</returns>
    public static IReadOnlyList<LibraryMod> Identify(
        string modsRoot, IEnumerable<string> modFolders, out IReadOnlyList<string> unidentified)
    {
        var mods = new List<LibraryMod>();
        var missing = new List<string>();

        foreach (var folder in modFolders.Distinct(StringComparer.Ordinal))
        {
            if (!Directory.Exists(folder))
                continue;

            var location = ModLibraryLayout.Locate(modsRoot, folder);
            var found = Directory.EnumerateFiles(folder)
                .Where(f => !ModLibraryFiles.IsSidecar(f))
                .Select(f => (Path: f, Source: SourceOf(f, location?.BackendId)))
                .Where(f => f.Source != null)
                .GroupBy(f => (f.Source!.BackendId.ToLowerInvariant(), f.Source.ModId))
                .ToList();

            if (found.Count == 0)
            {
                missing.Add(folder);
                continue;
            }

            foreach (var group in found)
            {
                var sources = group.Select(f => f.Source!).ToList();
                mods.Add(new LibraryMod
                {
                    Folder = folder,
                    Name = Path.GetFileName(folder),
                    BackendId = group.Key.Item1,
                    ModId = group.Key.ModId,
                    Game = sources.Select(s => s.GameDomain).FirstOrDefault(g => !string.IsNullOrEmpty(g))
                           ?? location?.Game,
                    Category = CategoryOf(modsRoot, folder),
                    FileIds = sources
                        .Select(s => s.FileId)
                        .Where(id => !string.IsNullOrEmpty(id))
                        .ToHashSet(StringComparer.Ordinal)!,
                    NewestDownload = group.Max(f => NexusFileName.ParseUploadTime(f.Path)
                                                    ?? new DateTimeOffset(File.GetLastWriteTimeUtc(f.Path)))
                });
            }
        }

        unidentified = missing;
        return mods;
    }

    /// <summary>Asks the mod's backend for its files and compares them with the folder's.</summary>
    /// <param name="mod">The mod.</param>
    /// <param name="backend">Its backend.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The newer files, or the error.</returns>
    public static async Task<LibraryModUpdate> CheckAsync(
        LibraryMod mod, IModBackend backend, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(backend);

        try
        {
            var files = await backend.GetModFilesAsync(mod.ModId, mod.Game, FileFilter.MainAndOptional, ct);
            var (newFiles, suggested) = Compare(files, mod);
            return new LibraryModUpdate { Mod = mod, NewFiles = newFiles, Suggested = suggested };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new LibraryModUpdate { Mod = mod, Error = ex.Message };
        }
    }

    /// <summary>Picks, from a backend's files, those newer than the mod's downloads.</summary>
    /// <param name="available">The files the backend offers.</param>
    /// <param name="mod">The mod as its folder identifies it.</param>
    /// <returns>Every newer file, newest first, and those suggested as updates.</returns>
    public static (IReadOnlyList<BackendModFile> New, IReadOnlyList<BackendModFile> Suggested) Compare(
        IReadOnlyList<BackendModFile> available, LibraryMod mod)
    {
        ArgumentNullException.ThrowIfNull(available);
        ArgumentNullException.ThrowIfNull(mod);

        var have = available.Where(f => mod.FileIds.Contains(f.FileId)).ToList();

        // The bar is the newest download as the backend dates it; failing that, as the
        // folder does. A file the folder already has is never new, whatever its date.
        var bar = have.Count > 0 && have.Any(f => f.UploadedAt.HasValue)
            ? have.Max(f => Utc(f.UploadedAt))
            : mod.NewestDownload;

        var newer = available
            .Where(f => !mod.FileIds.Contains(f.FileId))
            .Where(f => bar == null || (f.UploadedAt.HasValue && Utc(f.UploadedAt) > bar))
            .OrderByDescending(f => Utc(f.UploadedAt))
            .ToList();

        var suggested = new List<BackendModFile>();
        if (have.Count > 0)
        {
            foreach (var current in have)
            {
                var sameCategory = newer.Where(f => SameCategory(f, current)).ToList();

                // A file keeps its name across versions; with one download in its category
                // the newest of that category is its successor whatever it is called.
                var successor = sameCategory.FirstOrDefault(f => SameName(f, current))
                                ?? (have.Count(h => SameCategory(h, current)) == 1 ? sameCategory.FirstOrDefault() : null);

                if (successor != null && !suggested.Contains(successor))
                    suggested.Add(successor);
            }
        }
        else if (newer.Count > 0)
        {
            // The downloads are no longer listed (moved to old versions) or were never
            // recorded: the newest main file is the likely successor.
            suggested.Add(newer.FirstOrDefault(f => IsMain(f)) ?? newer[0]);
        }

        return (newer, suggested);
    }

    // Where a file came from: Modular's record, Mod Organizer's, or a NexusMods file name.
    private static DeclaredSource? SourceOf(string path, string? folderBackend) =>
        DependencyConventions.ReadDownloadRecord(path).Source
        ?? DependencyConventions.ReadModOrganizerMeta(path).Source
        ?? (folderBackend is null or BackendIds.NexusMods && NexusFileName.ParseModId(path) is { } id
            ? new DeclaredSource(BackendIds.NexusMods, id)
            : null);

    // <root>/<backend>/<game>/<category>/<mod>: the category is the folder between game and mod.
    private static string? CategoryOf(string modsRoot, string folder)
    {
        var relative = Path.GetRelativePath(modsRoot, folder)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return relative.Length == 4 ? relative[2] : null;
    }

    private static DateTimeOffset? Utc(DateTime? time) =>
        time is { } t ? new DateTimeOffset(DateTime.SpecifyKind(t, t.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : t.Kind)) : null;

    private static bool SameCategory(BackendModFile a, BackendModFile b) =>
        string.Equals(a.Category ?? string.Empty, b.Category ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static bool SameName(BackendModFile a, BackendModFile b) =>
        !string.IsNullOrWhiteSpace(a.DisplayName) &&
        string.Equals(a.DisplayName.Trim(), b.DisplayName?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool IsMain(BackendModFile file) =>
        string.IsNullOrEmpty(file.Category) || file.Category.Equals("main", StringComparison.OrdinalIgnoreCase);
}
