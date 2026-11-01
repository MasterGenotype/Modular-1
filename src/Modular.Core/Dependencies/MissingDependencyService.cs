using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Modular.Core.Archives;
using Modular.Core.Dependencies.Conventions;
using Modular.Core.Downloads;
using Modular.Core.Installers;
using Modular.Core.Metadata;
using Modular.Core.Utilities;
using Modular.Sdk.Archives;
using Modular.Sdk.Backends;
using Modular.Sdk.Backends.Common;
using Modular.Sdk.Modules;

namespace Modular.Core.Dependencies;

/// <summary>
/// How a missing dependency can be obtained.
/// </summary>
public enum MissingDependencyKind
{
    /// <summary>A mod on a backend, for the same game; its files can be queued.</summary>
    Downloadable,

    /// <summary>
    /// Somewhere a backend cannot download from: another site, or another game's page.
    /// All there is to offer is the link.
    /// </summary>
    External,

    /// <summary>
    /// Named by a mod's own metadata, which carries no backend id. The candidates are a
    /// name search, so a person has to confirm the match.
    /// </summary>
    Unidentified
}

/// <summary>
/// One mod that installed mods need and that is not installed.
/// </summary>
public sealed record MissingDependency
{
    /// <summary>How the dependency can be obtained.</summary>
    public required MissingDependencyKind Kind { get; init; }

    /// <summary>Name of the missing mod.</summary>
    public required string Name { get; init; }

    /// <summary>Names of the installed mods that need it.</summary>
    public IReadOnlyList<string> RequiredBy { get; init; } = [];

    /// <summary>Backend the mod is on. Null unless <see cref="Kind"/> is downloadable.</summary>
    public string? BackendId { get; init; }

    /// <summary>Game domain on that backend.</summary>
    public string? GameDomain { get; init; }

    /// <summary>The mod's id on that backend. Null unless downloadable.</summary>
    public string? ModId { get; init; }

    /// <summary>Where the mod can be found, when known.</summary>
    public string? Url { get; init; }

    /// <summary>
    /// What the requiring mods' authors wrote about the requirement, which is often the
    /// only thing saying which of the mod's files is needed.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>
    /// Possible matches from a name search, best first. Only for
    /// <see cref="MissingDependencyKind.Unidentified"/>.
    /// </summary>
    public IReadOnlyList<BackendMod> Candidates { get; init; } = [];

    /// <summary>
    /// Archives of this mod already in the download directory, newest first. When there
    /// are any, the mod needs installing rather than downloading again.
    /// </summary>
    public IReadOnlyList<string> DownloadedArchives { get; init; } = [];

    /// <summary>
    /// Whether every mod that lists it describes it as optional. A mod page lists what it
    /// can work with alongside what it needs, and only the author's note tells them apart.
    /// </summary>
    public bool Optional { get; init; }
}

/// <summary>
/// Two installed mods of which one says it cannot be used with the other.
/// </summary>
/// <param name="DeclaredBy">The mod that says so.</param>
/// <param name="ConflictsWith">The mod, or file, it names.</param>
/// <param name="Convention">Where the statement was read from.</param>
public sealed record InstalledConflict(string DeclaredBy, string ConflictsWith, string Convention);

/// <summary>
/// The outcome of looking for missing dependencies.
/// </summary>
public sealed record MissingDependencyReport
{
    /// <summary>What is missing, downloadable items first.</summary>
    public IReadOnlyList<MissingDependency> Items { get; init; } = [];

    /// <summary>How many installed mods had their requirements looked up on a backend.</summary>
    public int ModsChecked { get; init; }

    /// <summary>
    /// Installed mods that could not be traced to a backend, so their listed requirements
    /// are unknown.
    /// </summary>
    public IReadOnlyList<string> Untraceable { get; init; } = [];

    /// <summary>
    /// Requirements taken as met because a mod of that name is present in the game's mod
    /// folders, though Modular did not install it.
    /// </summary>
    public IReadOnlyList<string> SatisfiedElsewhere { get; init; } = [];

    /// <summary>Installed mods that declare they cannot be used together.</summary>
    public IReadOnlyList<InstalledConflict> Conflicts { get; init; } = [];

    /// <summary>Lookups that failed, as messages fit to show.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
}

/// <summary>
/// Works out which mods the installed mods need and do not have.
/// </summary>
/// <remarks>
/// <para>
/// Dependencies are recorded in as many ways as there are mod tools, and no one of them
/// is complete, so every one that applies is consulted:
/// </para>
/// <list type="bullet">
/// <item><description>
/// what a mod hub lists on the mod's page, through any backend that implements
/// <see cref="IRequirementsBackend"/>, which is also the only source that says where to
/// download the missing mod;
/// </description></item>
/// <item><description>
/// what the mod's packaging declares, in the formats of other mod managers, through
/// <see cref="DependencyConventions"/>;
/// </description></item>
/// <item><description>
/// what the mod files on disk declare in the game's own format, through the game module.
/// </description></item>
/// </list>
/// <para>
/// One level is resolved per call. A dependency's own requirements show up once it is
/// installed and the check is run again, which keeps each run's list reviewable.
/// </para>
/// </remarks>
public sealed class MissingDependencyService
{
    /// <summary>Backend id used for mods traced through a NexusMods archive name.</summary>
    public const string NexusBackendId = BackendIds.NexusMods;

    // How many name-search results to offer for a dependency with no backend id.
    private const int MaxCandidates = 5;

    private static readonly Regex NonWord = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

    // A note an author put in brackets after a requirement's name: "Codeware (No REDMOD version)".
    private static readonly Regex BracketedNote = new(@"\s*[\(\[][^\)\]]*[\)\]]", RegexOptions.Compiled);

    // An author's note that opens by calling the requirement optional: "(Optional) Adds…".
    private static readonly Regex OptionalNote = new(
        @"^\W*optional\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // The mod id an install was made with, when it is a NexusMods one: "16325", or
    // "16325-eotb" for one of that mod's files installed on its own.
    private static readonly Regex InstalledNexusId = new(
        @"^(?<id>\d+)(?:-[A-Za-z0-9][\w-]*)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IReadOnlyList<IModBackend> _backends;
    private readonly Func<ChangesetRecord, CancellationToken, Task<(GameInstallation Installation, IGameModule Module)?>> _resolveGame;
    private readonly string? _modsDirectory;
    private readonly IArchiveReaderFactory _archiveReaderFactory;
    private readonly ILogger? _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="backends">Backends to consult.</param>
    /// <param name="resolveGame">
    /// Resolves the game an installed changeset belongs to, or <c>null</c> when it cannot.
    /// </param>
    /// <param name="modsDirectory">
    /// The download directory, laid out as <c>&lt;root&gt;/&lt;backend&gt;/&lt;game&gt;/…</c> (see
    /// <see cref="ModLibraryLayout"/>). Used to find archives already downloaded, and to tell
    /// which game an archive is for when no game module claims it.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="archiveReaderFactory">Opens mod archives to read the metadata packed in them.</param>
    public MissingDependencyService(
        IEnumerable<IModBackend> backends,
        Func<ChangesetRecord, CancellationToken, Task<(GameInstallation Installation, IGameModule Module)?>> resolveGame,
        string? modsDirectory = null,
        ILogger? logger = null,
        IArchiveReaderFactory? archiveReaderFactory = null)
    {
        ArgumentNullException.ThrowIfNull(backends);
        ArgumentNullException.ThrowIfNull(resolveGame);

        _backends = backends.ToList();
        _resolveGame = resolveGame;
        _modsDirectory = modsDirectory;
        _logger = logger;
        _archiveReaderFactory = archiveReaderFactory ?? new ArchiveReaderFactory();
    }

    /// <summary>
    /// Finds what the given mods need that is not installed.
    /// </summary>
    /// <param name="targets">The installed mods to check.</param>
    /// <param name="installed">
    /// Every installed mod. A requirement that one of these satisfies is not missing.
    /// </param>
    /// <param name="includeUnmanaged">
    /// Whether to report dependencies declared by mod files Modular did not install, such
    /// as ones a game's own mod manager put in place. Right for a check of everything,
    /// wrong for a check of one mod.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The missing dependencies and what could not be checked.</returns>
    public async Task<MissingDependencyReport> FindAsync(
        IReadOnlyList<ChangesetRecord> targets,
        IReadOnlyList<ChangesetRecord> installed,
        bool includeUnmanaged = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(installed);

        var games = new GameCache(_resolveGame);
        var declarations = new Dictionary<string, ArchiveDeclarations>(StringComparer.Ordinal);
        var errors = new List<string>();
        var untraceable = new List<string>();
        var satisfiedElsewhere = new List<string>();
        var conflicts = new List<InstalledConflict>();
        var merged = new Dictionary<string, Accumulator>(StringComparer.OrdinalIgnoreCase);

        ArchiveDeclarations DeclarationsOf(ChangesetRecord record)
        {
            var key = record.ArchivePath ?? string.Empty;
            if (!declarations.TryGetValue(key, out var found))
                declarations[key] = found = DependencyConventions.Read(record.ArchivePath, _archiveReaderFactory);

            return found;
        }

        // What is installed, by hub identity and by the identifiers each mod's own
        // packaging says it provides, so a satisfied requirement can be dropped.
        var installedIds = new HashSet<(string Backend, string Domain, string ModId)>();
        var provided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in installed)
        {
            var game = await games.GetAsync(record, ct);
            var declared = DeclarationsOf(record);

            if (TryTrace(record, declared, game, out var source))
                installedIds.Add(Key(source));

            provided.UnionWith(declared.Provides);
        }

        var traced = new List<(ChangesetRecord Record, DeclaredSource Source)>();
        foreach (var record in targets)
        {
            var game = await games.GetAsync(record, ct);

            if (TryTrace(record, DeclarationsOf(record), game, out var source))
                traced.Add((record, source));
            else
                untraceable.Add(DisplayName(record));
        }

        // Mods present in the game's folders that Modular did not install, by name. A
        // requirement naming one of these is met, however it got there.
        var presentNames = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        async Task<HashSet<string>> PresentNamesAsync(ChangesetRecord record)
        {
            var game = await games.GetAsync(record, ct);
            if (game == null)
                return [];

            var (installation, module) = game.Value;
            if (presentNames.TryGetValue(installation.InstallPath, out var names))
                return names;

            names = [];
            try
            {
                names.UnionWith(module.ListPresentModNames(installation).Select(Normalize));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogDebug(ex, "Could not list present mods for {Game}", installation.GameKey);
            }

            names.Remove(string.Empty);
            return presentNames[installation.InstallPath] = names;
        }

        // 1. What a mod hub lists on each mod's page: one request per backend and game.
        var modsChecked = 0;

        foreach (var group in traced.GroupBy(t => (
                     Backend: t.Source.BackendId.ToLowerInvariant(),
                     Domain: (t.Source.GameDomain ?? string.Empty).ToLowerInvariant())))
        {
            ct.ThrowIfCancellationRequested();

            var backend = _backends.FirstOrDefault(b =>
                string.Equals(b.Id, group.Key.Backend, StringComparison.OrdinalIgnoreCase));

            if (backend is not IRequirementsBackend requirements)
            {
                errors.Add(backend == null
                    ? $"The '{group.Key.Backend}' backend is not enabled, so {group.Count()} mod(s) from it were not checked."
                    : $"{backend.DisplayName} cannot list mod requirements.");
                continue;
            }

            var domain = group.Key.Domain.Length == 0 ? null : group.First().Source.GameDomain;

            IReadOnlyDictionary<string, IReadOnlyList<ModRequirement>> fetched;
            try
            {
                fetched = await requirements.GetRequirementsAsync(
                    group.Select(t => t.Source.ModId).Distinct().ToList(), domain, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Requirement lookup failed on {Backend}", backend.Id);
                errors.Add($"Could not look up requirements on {backend.DisplayName}: {ex.Message}");
                continue;
            }

            foreach (var (record, source) in group)
            {
                if (!fetched.TryGetValue(source.ModId, out var listed))
                    continue;

                modsChecked++;
                var present = await PresentNamesAsync(record);

                foreach (var requirement in listed)
                {
                    var downloadable = !requirement.IsExternal && requirement.SameGame;

                    // A mod never requires itself, but pages have been known to say so.
                    if (downloadable &&
                        (installedIds.Contains(Key(source with { ModId = requirement.ModId })) ||
                         requirement.ModId == source.ModId))
                    {
                        continue;
                    }

                    if (present.Contains(Normalize(requirement.Name)) ||
                        present.Contains(Normalize(BracketedNote.Replace(requirement.Name, " "))))
                    {
                        if (!satisfiedElsewhere.Contains(requirement.Name, StringComparer.OrdinalIgnoreCase))
                            satisfiedElsewhere.Add(requirement.Name);
                        continue;
                    }

                    var key = downloadable
                        ? $"{backend.Id}|{domain}|{requirement.ModId}"
                        : $"external|{requirement.Url ?? requirement.Name}";

                    if (!merged.TryGetValue(key, out var item))
                    {
                        item = new Accumulator
                        {
                            Kind = downloadable
                                ? MissingDependencyKind.Downloadable
                                : MissingDependencyKind.External,
                            Name = requirement.Name,
                            BackendId = downloadable ? backend.Id : null,
                            GameDomain = downloadable ? domain : null,
                            ModId = downloadable ? requirement.ModId : null,
                            Url = requirement.Url
                        };
                        merged[key] = item;
                    }

                    item.AddRequiredBy(DisplayName(record));
                    item.AddNote(requirement.Notes);
                    item.AddStrength(optional: requirement.Notes != null && OptionalNote.IsMatch(requirement.Notes));
                }
            }
        }

        // 2. What each mod's own packaging declares, in other mod managers' formats.
        foreach (var record in targets)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var requirement in DeclarationsOf(record).Requirements)
            {
                if (provided.Contains(requirement.Identifier))
                {
                    if (requirement.Type == DependencyType.Incompatible)
                        conflicts.Add(new InstalledConflict(DisplayName(record), requirement.Name, requirement.Convention));
                    continue;
                }

                // A requirement stated as a file is checked against the game's folders.
                var onDisk = requirement.Convention == "fomod" && ExistsInGame(record, requirement.Identifier);

                if (requirement.Type == DependencyType.Incompatible)
                {
                    if (onDisk)
                        conflicts.Add(new InstalledConflict(DisplayName(record), requirement.Name, requirement.Convention));
                    continue;
                }

                if (requirement.Type != DependencyType.Required || onDisk)
                    continue;

                var game = await games.GetAsync(record, ct);
                var key = $"declared|{requirement.Convention}|{requirement.Identifier}";

                if (!merged.TryGetValue(key, out var item))
                {
                    item = new Accumulator
                    {
                        // With a link there is somewhere to send the user; without one,
                        // the name is all there is to search on.
                        Kind = requirement.Url != null
                            ? MissingDependencyKind.External
                            : MissingDependencyKind.Unidentified,
                        Name = requirement.Name,
                        Url = requirement.Url,
                        GameDomain = game != null ? DomainOf(game.Value.Module, game.Value.Installation) : null
                    };
                    merged[key] = item;
                }

                item.AddRequiredBy(DisplayName(record));
            }
        }

        // 3. What the mod files on disk declare in the game's own format.
        var seenGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in targets)
        {
            ct.ThrowIfCancellationRequested();

            var game = await games.GetAsync(record, ct);
            if (game == null || !seenGames.Add(game.Value.Installation.InstallPath))
                continue;

            var (installation, module) = game.Value;

            IReadOnlyList<UnmetDependency> unmet;
            IReadOnlyList<DeclaredConflict> declaredConflicts;
            try
            {
                unmet = module.FindUnmetDependencies(installation);
                declaredConflicts = module.FindDeclaredConflicts(installation);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Declared dependency scan failed for {Game}", installation.GameKey);
                errors.Add($"Could not read mod files for {installation.DisplayName ?? installation.GameKey}: {ex.Message}");
                continue;
            }

            // Which of the files on disk came from the mods being checked.
            var targetFiles = targets
                .Where(t => SameGame(t, record))
                .SelectMany(DeployedFileNames)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            bool InScope(string? file) =>
                includeUnmanaged || (file != null && targetFiles.Contains(file));

            foreach (var conflict in declaredConflicts.Where(c => InScope(c.DeclaredByFile)))
                conflicts.Add(new InstalledConflict(conflict.DeclaredBy, conflict.ConflictsWith, module.ModuleId));

            var domain = DomainOf(module, installation);

            foreach (var gap in unmet.Where(g => InScope(g.RequiredByFile)))
            {
                var key = $"declared|{installation.InstallPath}|{gap.Identifier}";
                if (!merged.TryGetValue(key, out var item))
                {
                    item = new Accumulator
                    {
                        Kind = MissingDependencyKind.Unidentified,
                        Name = gap.Name,
                        GameDomain = domain
                    };
                    merged[key] = item;
                }

                item.AddRequiredBy(gap.RequiredBy);
            }
        }

        // A dependency known only by name has no hub id, so offer a name search for each.
        var search = _backends.OfType<ISearchableBackend>().FirstOrDefault();

        foreach (var item in merged.Values.Where(i => i.Kind == MissingDependencyKind.Unidentified))
        {
            ct.ThrowIfCancellationRequested();

            if (search == null || string.IsNullOrEmpty(item.GameDomain))
                continue;

            var terms = NonWord.Replace(item.Name, " ").Trim();
            if (terms.Length == 0)
                continue;

            try
            {
                var result = await search.SearchModsAsync(
                    new ModSearchQuery
                    {
                        Terms = terms,
                        GameDomain = item.GameDomain,
                        PageSize = MaxCandidates,

                        // A dependency is needed whichever side of the line it is on.
                        AdultContent = null
                    },
                    ct);

                item.Candidates = result.Mods.Take(MaxCandidates).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Name search failed for {Name}", item.Name);
                errors.Add($"Could not search for '{item.Name}': {ex.Message}");
            }
        }

        // A missing mod whose archive is already in the download directory is not fetched
        // again. One listing of the directory per game serves every item.
        var downloads = new Dictionary<string, Dictionary<(string Backend, string ModId), List<string>>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var item in merged.Values.Where(i => i.Kind == MissingDependencyKind.Downloadable))
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(item.GameDomain) || item.BackendId == null || item.ModId == null)
                continue;

            var downloadsKey = $"{item.BackendId}|{item.GameDomain}";
            if (!downloads.TryGetValue(downloadsKey, out var index))
                downloads[downloadsKey] = index = IndexDownloads(item.BackendId, item.GameDomain);

            if (index.TryGetValue((item.BackendId.ToLowerInvariant(), item.ModId), out var archives))
                item.DownloadedArchives = archives;
        }

        return new MissingDependencyReport
        {
            Items = merged.Values
                .Select(i => i.Build())
                .OrderBy(i => i.Kind)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ModsChecked = modsChecked,
            Untraceable = untraceable.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            SatisfiedElsewhere = satisfiedElsewhere,
            Conflicts = conflicts.Distinct().ToList(),
            Errors = errors
        };
    }

    /// <summary>
    /// Lists the files of a mod that could satisfy a dependency.
    /// </summary>
    /// <param name="backendId">Backend the mod is on.</param>
    /// <param name="modId">The mod's id.</param>
    /// <param name="gameDomain">Game domain.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The mod, and its main and optional files with main files first.</returns>
    public async Task<(BackendMod Mod, IReadOnlyList<BackendModFile> Files)> GetFilesAsync(
        string backendId, string modId, string? gameDomain, CancellationToken ct = default)
    {
        var backend = _backends.FirstOrDefault(b =>
                          string.Equals(b.Id, backendId, StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException($"Backend '{backendId}' is not available.");

        var files = await backend.GetModFilesAsync(modId, gameDomain, FileFilter.MainAndOptional, ct);

        var mod = await backend.GetModInfoAsync(modId, gameDomain, ct) ?? new BackendMod
        {
            ModId = modId,
            Name = $"Mod {modId}",
            GameDomain = gameDomain,
            BackendId = backend.Id
        };

        return (mod, files
            .OrderBy(f => IsMain(f) ? 0 : 1)
            .ThenByDescending(f => f.UploadedAt ?? DateTime.MinValue)
            .ToList());
    }

    /// <summary>
    /// Picks the file to download when the choice is not in doubt.
    /// </summary>
    /// <param name="files">A mod's candidate files.</param>
    /// <returns>
    /// The only main file, or the only file; <c>null</c> when a person has to choose.
    /// </returns>
    public static BackendModFile? PickDefaultFile(IReadOnlyList<BackendModFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var mains = files.Where(IsMain).ToList();

        if (mains.Count == 1)
            return mains[0];

        return mains.Count == 0 && files.Count == 1 ? files[0] : null;
    }

    /// <summary>
    /// Reads the NexusMods mod id out of a downloaded archive's file name.
    /// </summary>
    /// <param name="archivePath">Path or file name of the archive.</param>
    /// <returns>The mod id, or <c>null</c> when the name is not in NexusMods' form.</returns>
    public static string? ParseNexusModId(string? archivePath) =>
        NexusFileName.ParseModId(archivePath);

    // Settles which hub mod an installed archive is, and which game the hub files it
    // under. The packaging says which mod; where it is installed says which game.
    private bool TryTrace(
        ChangesetRecord record,
        ArchiveDeclarations declared,
        (GameInstallation Installation, IGameModule Module)? game,
        out DeclaredSource source)
    {
        // The archive's own records come first. An archive kept in the archive store has
        // none beside it, and a bare .pak has no id in its name, so the id the mod was
        // installed with stands in.
        source = declared.Source ?? SourceFromInstall(record, _modsDirectory)!;
        if (source == null)
            return false;

        // GameBanana ids are site-wide; only a hub with per-game ids needs the game.
        if (!string.Equals(source.BackendId, BackendIds.NexusMods, StringComparison.OrdinalIgnoreCase))
            return true;

        var domain = source.GameDomain
                     ?? (game != null ? DomainOf(game.Value.Module, game.Value.Installation) : null)
                     ?? DomainFromDownloadPath(record.ArchivePath);

        if (string.IsNullOrEmpty(domain))
            return false;

        source = source with { GameDomain = domain };
        return true;
    }

    // Other hubs number their mods too, so an archive filed under another hub's folder of
    // the mods directory is never taken for a NexusMods one.
    private static DeclaredSource? SourceFromInstall(ChangesetRecord record, string? modsDirectory)
    {
        if (record.ModId == null || InstalledNexusId.Match(record.ModId) is not { Success: true } match)
            return null;

        if (ModLibraryLayout.Locate(modsDirectory, record.ArchivePath) is { } location &&
            !string.Equals(location.BackendId, BackendIds.NexusMods, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var id = match.Groups["id"].Value.TrimStart('0');
        return id.Length > 0 ? new DeclaredSource(BackendIds.NexusMods, id) : null;
    }

    // Lists a game's downloaded archives by the hub mod each came from, newest first. The
    // download record beside a file says which mod; failing that, a NexusMods file name does.
    private Dictionary<(string Backend, string ModId), List<string>> IndexDownloads(string backendId, string gameDomain)
    {
        var index = new Dictionary<(string Backend, string ModId), List<string>>();
        if (string.IsNullOrEmpty(_modsDirectory))
            return index;

        // NexusMods ids are per game, so only that game's folder can hold the mod; the
        // folder of a mods directory not yet reorganized is looked in too. Other hubs'
        // ids are site-wide, and their game folders are named after the game rather than
        // its id, so all of the hub's folder is looked in.
        var roots = string.Equals(backendId, NexusBackendId, StringComparison.OrdinalIgnoreCase)
            ? new[]
            {
                ModLibraryLayout.GameFolder(_modsDirectory, NexusBackendId, gameDomain),
                Path.Combine(_modsDirectory, gameDomain)
            }
            : [ModLibraryLayout.BackendFolder(_modsDirectory, backendId)];

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };

        foreach (var root in roots.Where(Directory.Exists))
        {
            IndexDownloadsIn(root, options, index);
        }

        foreach (var archives in index.Values)
            archives.Sort((a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));

        return index;
    }

    private void IndexDownloadsIn(
        string root,
        EnumerationOptions options,
        Dictionary<(string Backend, string ModId), List<string>> index)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", options))
            {
                if (IsDownloadBookkeeping(file))
                    continue;

                var source = DependencyConventions.ReadDownloadRecord(file).Source;
                var backendId = source?.BackendId ?? NexusBackendId;
                var modId = source?.ModId ?? NexusFileName.ParseModId(file);
                if (modId == null)
                    continue;

                var key = (backendId.ToLowerInvariant(), modId);
                if (!index.TryGetValue(key, out var archives))
                    index[key] = archives = [];

                archives.Add(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogDebug(ex, "Could not list downloads in {Root}", root);
        }
    }

    // Records and partial data the downloader keeps beside a file, which are not mods.
    private static bool IsDownloadBookkeeping(string file) =>
        file.EndsWith(DependencyConventions.DownloadRecordSuffix, StringComparison.OrdinalIgnoreCase) ||
        file.EndsWith(DependencyConventions.ModOrganizerSuffix, StringComparison.OrdinalIgnoreCase) ||
        file.EndsWith(ResumableDownloader.PartSuffix, StringComparison.OrdinalIgnoreCase) ||
        file.EndsWith(ResumableDownloader.StateSuffix, StringComparison.OrdinalIgnoreCase);

    private static (string Backend, string Domain, string ModId) Key(DeclaredSource source) =>
        (source.BackendId.ToLowerInvariant(), (source.GameDomain ?? string.Empty).ToLowerInvariant(), source.ModId);

    // Letters and digits only, so "Compatibility Framework" meets "CompatibilityFramework".
    private static string Normalize(string name) =>
        NonWord.Replace(name, string.Empty).ToLowerInvariant();

    // Whether a file a mod's packaging names exists in the game, in either of the places
    // such paths are written relative to. Case is ignored, as it is on the platforms
    // these files were written for.
    private static bool ExistsInGame(ChangesetRecord record, string relativePath)
    {
        if (string.IsNullOrEmpty(record.TargetDirectory))
            return false;

        return new[] { record.TargetDirectory, Path.Combine(record.TargetDirectory, "Data") }
            .Any(root => ExistsIgnoringCase(root, relativePath));
    }

    private static bool ExistsIgnoringCase(string root, string relativePath)
    {
        var current = root;

        try
        {
            foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Directory.Exists(current))
                    return false;

                var next = Directory.EnumerateFileSystemEntries(current)
                    .FirstOrDefault(e => string.Equals(
                        Path.GetFileName(e), segment, StringComparison.OrdinalIgnoreCase));

                if (next == null)
                    return false;

                current = next;
            }

            return File.Exists(current) || Directory.Exists(current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Game ids are NexusMods slugs; a module may also list the Steam AppID as a string.
    private static string? DomainOf(IGameModule module, GameInstallation installation)
    {
        var fromModule = module.GameIds.FirstOrDefault(id => id.Length > 0 && !id.All(char.IsDigit));
        if (fromModule != null)
            return fromModule;

        return !string.IsNullOrEmpty(installation.GameId) && !installation.GameId.All(char.IsDigit)
            ? installation.GameId
            : null;
    }

    private string? DomainFromDownloadPath(string? archivePath)
    {
        if (string.IsNullOrEmpty(_modsDirectory) || string.IsNullOrEmpty(archivePath))
            return null;

        if (ModLibraryLayout.Locate(_modsDirectory, archivePath) is { } located)
        {
            return string.Equals(located.BackendId, NexusBackendId, StringComparison.OrdinalIgnoreCase)
                ? located.Game
                : null;
        }

        // A mods directory not yet reorganized keeps NexusMods games in the root.

        var relative = Path.GetRelativePath(Path.GetFullPath(_modsDirectory), Path.GetFullPath(archivePath));
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return null;

        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Length > 1 && !parts[0].StartsWith('.') && !ModLibraryLayout.IsBackendFolder(parts[0])
            ? parts[0]
            : null;
    }

    private static bool SameGame(ChangesetRecord a, ChangesetRecord b) =>
        !string.IsNullOrEmpty(a.GameKey)
            ? string.Equals(a.GameKey, b.GameKey, StringComparison.OrdinalIgnoreCase)
            : string.Equals(a.TargetDirectory, b.TargetDirectory, StringComparison.OrdinalIgnoreCase);

    // Names of the files a changeset put in the game's folders, from whichever list its
    // kind of install recorded.
    private static IEnumerable<string> DeployedFileNames(ChangesetRecord record)
    {
        if (string.IsNullOrEmpty(record.OperationsJson))
            yield break;

        JsonElement operations;
        try
        {
            operations = JsonSerializer.Deserialize<JsonElement>(record.OperationsJson);
        }
        catch (JsonException)
        {
            yield break;
        }

        if (operations.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var property in new[] { "deployedFiles", "installedFiles" })
        {
            if (!operations.TryGetProperty(property, out var files) ||
                files.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var file in files.EnumerateArray())
            {
                if (file.ValueKind == JsonValueKind.String && file.GetString() is { Length: > 0 } path)
                    yield return Path.GetFileName(path.Replace('\\', '/'));
            }
        }
    }

    private static string DisplayName(ChangesetRecord record) =>
        !string.IsNullOrWhiteSpace(record.ModId)
            ? record.ModId
            : Path.GetFileNameWithoutExtension(record.ArchivePath) ?? record.ChangesetId;

    /// <summary>
    /// Whether a file is one of a mod's main files, as opposed to an optional extra.
    /// </summary>
    /// <param name="file">A mod file.</param>
    /// <returns><c>true</c> for a main file.</returns>
    public static bool IsMain(BackendModFile file) =>
        string.Equals(file.Category, "main", StringComparison.OrdinalIgnoreCase);

    // Resolving a game walks Steam's libraries, and every mod of a game resolves alike.
    private sealed class GameCache(
        Func<ChangesetRecord, CancellationToken, Task<(GameInstallation Installation, IGameModule Module)?>> resolve)
    {
        private readonly Dictionary<string, (GameInstallation, IGameModule)?> _games =
            new(StringComparer.OrdinalIgnoreCase);

        public async Task<(GameInstallation Installation, IGameModule Module)?> GetAsync(
            ChangesetRecord record, CancellationToken ct)
        {
            var key = $"{record.GameKey}|{record.TargetDirectory}";
            if (_games.TryGetValue(key, out var cached))
                return cached;

            (GameInstallation, IGameModule)? game;
            try
            {
                game = await resolve(record, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                game = null;
            }

            _games[key] = game;
            return game;
        }
    }

    private sealed class Accumulator
    {
        private readonly List<string> _requiredBy = [];
        private readonly List<string> _notes = [];
        private bool _listedAsOptional;
        private bool _listedAsRequired;

        public MissingDependencyKind Kind { get; init; }
        public string Name { get; init; } = string.Empty;
        public string? BackendId { get; init; }
        public string? GameDomain { get; init; }
        public string? ModId { get; init; }
        public string? Url { get; init; }
        public IReadOnlyList<BackendMod> Candidates { get; set; } = [];
        public IReadOnlyList<string> DownloadedArchives { get; set; } = [];

        // One mod needing it outright makes it needed, whatever the others say.
        public void AddStrength(bool optional)
        {
            if (optional)
                _listedAsOptional = true;
            else
                _listedAsRequired = true;
        }

        public void AddRequiredBy(string name)
        {
            if (!_requiredBy.Contains(name, StringComparer.OrdinalIgnoreCase))
                _requiredBy.Add(name);
        }

        public void AddNote(string? note)
        {
            if (!string.IsNullOrWhiteSpace(note) && !_notes.Contains(note, StringComparer.OrdinalIgnoreCase))
                _notes.Add(note);
        }

        public MissingDependency Build() => new()
        {
            Kind = Kind,
            Name = Name,
            RequiredBy = _requiredBy.ToList(),
            BackendId = BackendId,
            GameDomain = GameDomain,
            ModId = ModId,
            Url = Url,
            Notes = _notes.ToList(),
            Candidates = Candidates,
            DownloadedArchives = DownloadedArchives,
            Optional = _listedAsOptional && !_listedAsRequired
        };
    }
}
