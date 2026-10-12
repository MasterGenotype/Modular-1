using Modular.Sdk.Archives;
using Modular.Sdk.Installers;

namespace Modular.Core.Installers.BaldursGate3;

/// <summary>
/// Fingerprints a Baldur's Gate 3 mod archive and decides where each file belongs.
/// </summary>
/// <remarks>
/// <para>
/// BG3 has two unrelated mod locations. A <c>.pak</c> goes in the user <c>Mods</c> folder
/// (under <c>Larian Studios/Baldur's Gate 3/</c> in the user's local data, not in the
/// install). Loose files go beneath <c>&lt;install&gt;/Data/</c>, mirroring the virtual
/// paths inside the game's own paks, and override every pak regardless of load order.
/// </para>
/// <para>
/// Loose archives are packed from whatever folder the author happened to zip, so the tree is
/// anchored on the first folder name the game's layout recognises: <c>Data</c> itself, one
/// of its children (<c>Public</c>, <c>Mods</c>, <c>Generated</c>, <c>Localization</c>), or
/// one of <c>Public</c>'s children (<c>Game</c>, <c>Shared</c>, <c>Gustav</c> …). Anything
/// above the anchor is a wrapper folder and is dropped.
/// </para>
/// </remarks>
public static class BG3ArchiveAnalyzer
{
    /// <summary>Name of the module root holding the user <c>Mods</c> folder.</summary>
    public const string ModsRoot = "bg3mods";

    /// <summary>The loose-file folder, relative to the game root.</summary>
    public const string DataFolder = "Data";

    private const string BinFolder = "bin";

    /// <summary>The Script Extender's loader, as it is named in <c>bin/</c>.</summary>
    public const string ScriptExtenderLoader = "DWrite.dll";

    // What a Script Extender release contains. Both belong in bin/.
    private static readonly string[] ScriptExtenderFiles =
        [ScriptExtenderLoader, "ScriptExtenderSettings.json"];

    // Folders that sit directly in Data/.
    private static readonly string[] DataChildren =
        ["Public", "Mods", "Generated", "Localization"];

    // Folders that sit in Data/Public/. An archive rooted at one of these was zipped one
    // level too deep, which is common for UI and texture replacers ("Game/GUI/Assets/...").
    private static readonly string[] PublicChildren =
    [
        "Game", "Shared", "SharedDev", "Gustav", "GustavDev", "GustavX", "Engine",
        "Honour", "HonourX"
    ];

    /// <summary>
    /// Analyzes archive entries and produces a <see cref="BG3InstallLayout"/>.
    /// </summary>
    /// <param name="entries">The archive's entries.</param>
    /// <returns>The layout; unrecognised when no entry could be routed.</returns>
    public static BG3InstallLayout Analyze(
        IReadOnlyList<ArchiveEntry> entries,
        IReadOnlyDictionary<string, string>? selections = null)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var choices = FindVariants(entries, selections, out var excluded);

        var routes = new Dictionary<string, FileRoute>(StringComparer.Ordinal);
        var hasPak = false;
        var hasDeclaredLoose = false;
        var hasGuessedLoose = false;
        var hasNative = false;

        foreach (var entry in entries.Where(e => !e.IsDirectory))
        {
            var path = Normalize(entry.FullName);
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
                continue;

            // Part of an alternative the user did not pick.
            if (excluded(path))
                continue;

            // A pak always belongs in the user Mods folder, flat, wherever it sat in the
            // archive. One left under Data/ would never be offered by the game's mod list.
            if (path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
            {
                routes[entry.FullName] = FileRoute
                    .Heuristic(segments[^1], PackageTypes.Bg3Pak)
                    .InRoot(ModsRoot);
                hasPak = true;
                continue;
            }

            // Script Extender ships its loader at the top of its archive; it only works
            // from beside the game executable.
            if (ScriptExtenderFiles.Contains(segments[^1], StringComparer.OrdinalIgnoreCase))
            {
                var canonical = ScriptExtenderFiles.First(f =>
                    string.Equals(f, segments[^1], StringComparison.OrdinalIgnoreCase));

                routes[entry.FullName] = FileRoute.Declared(
                    $"{BinFolder}/{canonical}", PackageTypes.Bg3ScriptExtender);
                hasNative = true;
                continue;
            }

            var dataIndex = IndexOfSegment(segments, DataFolder);
            var dataChild = IndexOfAnySegment(segments, DataChildren);
            var publicChild = IndexOfAnySegment(segments, PublicChildren);

            // "Data" only names the game's Data folder when it comes before any folder
            // that lives inside it. Stats sit in Public/<Mod>/Stats/Generated/Data/, and
            // that inner "Data" is just part of the file's path.
            var dataIsAnchor = dataIndex >= 0 &&
                               (dataChild < 0 || dataIndex < dataChild) &&
                               (publicChild < 0 || dataIndex < publicChild);

            if (dataIsAnchor && dataIndex < segments.Length - 1)
            {
                routes[entry.FullName] = FileRoute.Declared(
                    $"{DataFolder}/{Join(segments, dataIndex + 1)}", PackageTypes.Bg3LooseData);
                hasDeclaredLoose = true;
                continue;
            }

            if (dataChild >= 0 && dataChild < segments.Length - 1)
            {
                routes[entry.FullName] = FileRoute.Declared(
                    $"{DataFolder}/{Canonical(segments, dataChild, DataChildren)}",
                    PackageTypes.Bg3LooseData);
                hasDeclaredLoose = true;
                continue;
            }

            // bin/ holds the executable; Script Extender and NativeMods ship into it.
            var binIndex = IndexOfSegment(segments, BinFolder);
            if (binIndex >= 0 && binIndex < segments.Length - 1)
            {
                routes[entry.FullName] = FileRoute.Declared(
                    $"{BinFolder}/{Join(segments, binIndex + 1)}", PackageTypes.Bg3Native);
                hasNative = true;
                continue;
            }

            if (publicChild >= 0 && publicChild < segments.Length - 1)
            {
                routes[entry.FullName] = FileRoute.Heuristic(
                    $"{DataFolder}/Public/{Canonical(segments, publicChild, PublicChildren)}",
                    PackageTypes.Bg3LooseData);
                hasGuessedLoose = true;
            }

            // Anything else is a readme, a preview image or mod-manager metadata
            // (info.json) and is deliberately left unrouted.
        }

        var hasLoose = hasDeclaredLoose || hasGuessedLoose;

        if (routes.Count == 0)
        {
            return new BG3InstallLayout
            {
                Confidence = 0,
                Reason = "No .pak and no Data/, Public/ or bin/ tree found"
            };
        }

        var kinds = new List<string>();
        if (hasPak) kinds.Add("pak for the user Mods folder");
        if (hasLoose) kinds.Add("loose files for Data/");
        if (hasNative) kinds.Add("native files for bin/");

        return new BG3InstallLayout
        {
            HasPak = hasPak,
            HasLooseData = hasLoose,
            // A folder name borrowed from inside Public/ is a weaker anchor than a path
            // the archive spelled out.
            Confidence = hasPak || hasDeclaredLoose || hasNative ? 0.9 : 0.8,
            Reason = "BG3 mod: " + string.Join(", ", kinds),
            FileRoutes = routes,
            Choices = choices
        };
    }

    /// <summary>
    /// Finds paks an archive offers in several versions, and settles which to install.
    /// </summary>
    /// <param name="entries">The archive's entries.</param>
    /// <param name="selections">Answers already given, as choice id to option id.</param>
    /// <param name="excluded">Tells whether a path belongs to an alternative that lost.</param>
    /// <returns>One choice per set of alternatives.</returns>
    /// <remarks>
    /// The signal is the same pak file name in more than one folder: "Option A/Mod.pak"
    /// beside "Option B/Mod.pak". Every pak goes flat into one folder, so two of a name
    /// can never both be installed; the archive is offering a choice whether or not it
    /// says so. Paks that vary across the same set of folders are one decision, not
    /// several.
    /// </remarks>
    private static List<InstallChoice> FindVariants(
        IReadOnlyList<ArchiveEntry> entries,
        IReadOnlyDictionary<string, string>? selections,
        out Func<string, bool> excluded)
    {
        var files = entries
            .Where(e => !e.IsDirectory)
            .Select(e => Normalize(e.FullName))
            .Where(p => p.Length > 0)
            .ToList();

        // Pak name -> the folders it appears in, in archive order.
        var folders = files
            .Where(p => p.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
            .GroupBy(FileNameOf, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Folders: g.Select(FolderOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
            .Where(g => g.Folders.Count > 1)
            .ToList();

        var choices = new List<InstallChoice>();
        var losingFolders = new List<string>();
        var losingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var set in folders.GroupBy(
                     g => string.Join('|', g.Folders.Order(StringComparer.OrdinalIgnoreCase)),
                     StringComparer.OrdinalIgnoreCase))
        {
            var options = set.First().Folders;
            var names = set.Select(g => g.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
            var id = names[0];

            var common = CommonFolder(options);
            var choice = new InstallChoice
            {
                Id = id,
                Title = names.Count == 1
                    ? $"Which version of {names[0]}?"
                    : $"Which version of {names[0]} and {names.Count - 1} other pak(s)?",
                Options = options.Select(folder => new InstallChoiceOption
                {
                    Id = folder,
                    Label = folder.Length == common.Length
                        ? "(top of the archive)"
                        : folder[common.Length..].Trim('/'),
                    FileCount = folder.Length == 0
                        ? names.Count
                        : files.Count(f => IsUnder(f, folder))
                }).ToList()
            };

            var given = selections != null && selections.TryGetValue(id, out var answer)
                ? options.FirstOrDefault(o => string.Equals(o, answer, StringComparison.OrdinalIgnoreCase))
                : null;

            choice.IsResolved = given != null;
            choice.SelectedOptionId = given ?? options[0];
            choices.Add(choice);

            foreach (var folder in options)
            {
                if (string.Equals(folder, choice.SelectedOptionId, StringComparison.OrdinalIgnoreCase))
                    continue;

                // A folder that holds the winner as well, or the archive root, cannot be
                // dropped wholesale; only its copies of the contested paks go.
                if (folder.Length == 0 || IsUnder(choice.SelectedOptionId + "/", folder))
                {
                    foreach (var name in names)
                        losingFiles.Add(folder.Length == 0 ? name : $"{folder}/{name}");
                }
                else
                {
                    losingFolders.Add(folder);
                }
            }
        }

        excluded = path =>
            losingFiles.Contains(path) || losingFolders.Any(folder => IsUnder(path, folder));

        return choices;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    private static string FileNameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static string FolderOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    private static bool IsUnder(string path, string folder) =>
        path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);

    // The folder every option shares, so labels show only what tells them apart.
    private static string CommonFolder(IReadOnlyList<string> folders)
    {
        var first = folders[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
        var depth = first.Length;

        foreach (var folder in folders.Skip(1))
        {
            var parts = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var shared = 0;
            while (shared < depth && shared < parts.Length &&
                   string.Equals(first[shared], parts[shared], StringComparison.OrdinalIgnoreCase))
            {
                shared++;
            }

            depth = shared;
        }

        return string.Join('/', first.Take(depth));
    }

    private static int IndexOfSegment(string[] segments, string name) =>
        Array.FindIndex(segments, s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));

    private static int IndexOfAnySegment(string[] segments, string[] names) =>
        Array.FindIndex(segments, s => names.Contains(s, StringComparer.OrdinalIgnoreCase));

    // The anchor folder takes the game's own casing, so "public/" in an archive does not
    // create a second folder beside "Public/" on a case-sensitive filesystem.
    private static string Canonical(string[] segments, int anchor, string[] names)
    {
        var name = names.First(n =>
            string.Equals(n, segments[anchor], StringComparison.OrdinalIgnoreCase));

        return $"{name}/{Join(segments, anchor + 1)}";
    }

    private static string Join(string[] segments, int start) =>
        string.Join('/', segments, start, segments.Length - start);
}
