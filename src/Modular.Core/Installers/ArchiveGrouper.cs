using Microsoft.Extensions.Logging;
using Modular.Core.Dependencies.Conventions;
using Modular.Core.Utilities;
using Modular.Sdk.Backends;
using Modular.Sdk.Backends.Common;

namespace Modular.Core.Installers;

/// <summary>
/// An archive in the install list, with what its mod's page calls it.
/// </summary>
/// <param name="Path">The archive.</param>
/// <param name="Category">
/// The page's file type: <c>main</c>, <c>optional</c>, <c>old_version</c>, <c>update</c>,
/// <c>miscellaneous</c>; <c>null</c> when it is not known.
/// </param>
/// <param name="Label">The page's name for the file, when known.</param>
public sealed record QueuedArchive(string Path, string? Category = null, string? Label = null)
{
    /// <summary>
    /// Whether this file is one of the mod's versions rather than an add-on to them: a
    /// main file or an old version of one.
    /// </summary>
    public bool IsVersion =>
        string.Equals(Category, "main", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Category, "old_version", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The archives in the install list that are files of one mod.
/// </summary>
/// <param name="Key">What identifies the mod.</param>
/// <param name="Name">The mod's name, for showing.</param>
/// <param name="Archives">Its archives, in the order they were queued.</param>
public sealed record ArchiveGroup(string Key, string Name, IReadOnlyList<QueuedArchive> Archives)
{
    /// <summary>Whether the mod's page said what each of its archives is.</summary>
    public bool KnowsFileTypes => Archives.All(a => a.Category != null);
}

/// <summary>
/// Groups archives in the install list by the mod they are files of, so that a mod with
/// several queued, such as two versions of it, can be asked about once.
/// </summary>
/// <remarks>
/// <para>
/// An archive's mod comes from, most reliable first: the download record beside it; the
/// NexusMods id in its file name; the mod folder it sits in inside the mods directory
/// (<c>&lt;backend&gt;/&lt;game&gt;[/&lt;category&gt;]/&lt;mod&gt;/</c>). An archive with none of these
/// is a mod of its own.
/// </para>
/// <para>
/// Only a mod with more than one archive queued has its page read, for what each file
/// is: NexusMods says main, optional, old version and so on; mod.io marks the live file
/// main and the rest old versions.
/// </para>
/// </remarks>
public sealed class ArchiveGrouper
{
    private readonly IReadOnlyList<IModBackend> _backends;
    private readonly string? _modsDirectory;
    private readonly ILogger? _logger;

    /// <summary>Creates the grouper.</summary>
    /// <param name="backends">Backends to read mod pages from.</param>
    /// <param name="modsDirectory">The mods directory, for telling mods apart by folder.</param>
    /// <param name="logger">Optional logger.</param>
    public ArchiveGrouper(IEnumerable<IModBackend> backends, string? modsDirectory, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(backends);

        _backends = backends.ToList();
        _modsDirectory = modsDirectory;
        _logger = logger;
    }

    /// <summary>
    /// Groups the archives by mod, reading the page of each mod with more than one.
    /// </summary>
    /// <param name="archives">The install list.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>One group per mod, in the order the mods were first queued.</returns>
    public async Task<IReadOnlyList<ArchiveGroup>> GroupAsync(
        IReadOnlyList<string> archives, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(archives);

        var groups = new List<ArchiveGroup>();

        foreach (var (identity, paths) in Identify(archives, _modsDirectory))
        {
            ct.ThrowIfCancellationRequested();

            var queued = paths.Select(p => new QueuedArchive(p)).ToList();
            if (queued.Count > 1 && identity.Source != null)
                queued = await AddFileTypesAsync(identity.Source, queued, ct);

            groups.Add(new ArchiveGroup(identity.Key, identity.Name, queued));
        }

        return groups;
    }

    /// <summary>
    /// Works out which mod each archive is a file of, without reading any page.
    /// </summary>
    /// <param name="archives">The install list.</param>
    /// <param name="modsDirectory">The mods directory.</param>
    /// <returns>Each mod's identity and archives, in the order first queued.</returns>
    public static IReadOnlyList<(ModIdentity Identity, IReadOnlyList<string> Archives)> Identify(
        IReadOnlyList<string> archives, string? modsDirectory)
    {
        ArgumentNullException.ThrowIfNull(archives);

        var order = new List<string>();
        var byKey = new Dictionary<string, (ModIdentity Identity, List<string> Archives)>(StringComparer.OrdinalIgnoreCase);

        foreach (var archive in archives)
        {
            var identity = IdentityOf(archive, modsDirectory);
            if (!byKey.TryGetValue(identity.Key, out var entry))
            {
                byKey[identity.Key] = entry = (identity, []);
                order.Add(identity.Key);
            }

            entry.Archives.Add(archive);
        }

        return order.Select(k => (byKey[k].Identity, (IReadOnlyList<string>)byKey[k].Archives)).ToList();
    }

    private static ModIdentity IdentityOf(string archive, string? modsDirectory)
    {
        var folder = Path.GetDirectoryName(archive) ?? string.Empty;
        var located = ModLibraryLayout.Locate(modsDirectory, archive);

        // The mod folder: inside a game folder, and not the game folder itself.
        var inModFolder = located != null &&
                          !string.Equals(
                              Path.GetFullPath(folder),
                              Path.GetFullPath(ModLibraryLayout.GameFolder(modsDirectory!, located.Value.BackendId, located.Value.Game)),
                              StringComparison.Ordinal);
        var folderName = inModFolder ? Path.GetFileName(folder) : null;

        // 1. The download record says which mod, on which site.
        if (DependencyConventions.ReadDownloadRecord(archive).Source is { } recorded)
        {
            var domain = recorded.GameDomain ??
                         (located?.BackendId == recorded.BackendId ? located.Value.Game : null);
            var source = recorded with { GameDomain = domain };
            return new ModIdentity(
                $"{recorded.BackendId}|{domain}|{recorded.ModId}".ToLowerInvariant(),
                folderName ?? Path.GetFileNameWithoutExtension(archive),
                source);
        }

        // 2. A NexusMods file name carries the mod id. Ids are per game, so the game is
        //    part of the key; without one, the folder stands in for it.
        if (NexusFileName.ParseModId(archive) is { } nexusId)
        {
            var domain = located?.BackendId == BackendIds.NexusMods ? located.Value.Game : null;
            return new ModIdentity(
                $"{BackendIds.NexusMods}|{domain ?? folder}|{nexusId}".ToLowerInvariant(),
                folderName ?? Path.GetFileNameWithoutExtension(archive),
                domain != null ? new DeclaredSource(BackendIds.NexusMods, nexusId, domain) : null);
        }

        // 3. A mod folder in the mods directory holds one mod's files.
        if (folderName != null)
            return new ModIdentity("folder|" + Path.GetFullPath(folder), folderName, null);

        return new ModIdentity("file|" + Path.GetFullPath(archive), Path.GetFileNameWithoutExtension(archive), null);
    }

    // Reads what the mod's page calls each archive: by the recorded file id, else by name.
    private async Task<List<QueuedArchive>> AddFileTypesAsync(
        DeclaredSource source, List<QueuedArchive> queued, CancellationToken ct)
    {
        var backend = _backends.FirstOrDefault(b =>
            string.Equals(b.Id, source.BackendId, StringComparison.OrdinalIgnoreCase));
        if (backend == null)
            return queued;

        List<BackendModFile> files;
        try
        {
            files = await backend.GetModFilesAsync(
                source.ModId, source.GameDomain, new FileFilter { IncludeArchived = true }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Could not list the files of {Backend} mod {ModId}", source.BackendId, source.ModId);
            return queued;
        }

        return queued.Select(archive =>
        {
            var fileId = DependencyConventions.ReadDownloadRecord(archive.Path).Source?.FileId;
            var name = Path.GetFileName(archive.Path);

            var file = files.FirstOrDefault(f => fileId != null && f.FileId == fileId)
                       ?? files.FirstOrDefault(f => string.Equals(
                           FileUtils.SanitizeFilename(f.FileName), name, StringComparison.OrdinalIgnoreCase));

            return file == null
                ? archive
                : archive with { Category = file.Category, Label = file.DisplayName ?? file.FileName };
        }).ToList();
    }
}

/// <summary>
/// What an archive in the install list is a file of.
/// </summary>
/// <param name="Key">Equal for archives of the same mod.</param>
/// <param name="Name">The mod's name, for showing.</param>
/// <param name="Source">The mod on its site, when known; its page says what each file is.</param>
public sealed record ModIdentity(string Key, string Name, DeclaredSource? Source);
