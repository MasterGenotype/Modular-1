using System.Text.Json;
using System.Text.RegularExpressions;
using Modular.Sdk.Archives;
using Modular.Sdk.Installers;

namespace Modular.Core.Installers.Enshrouded;

/// <summary>
/// Works out where each file of an Enshrouded mod archive goes.
/// </summary>
/// <remarks>
/// <para>
/// Enshrouded has no official mod support. Mods are run by EML (Enshrouded Mod Loader,
/// github.com/Brabb3l/kfc-parser): a <c>dbghelp.dll</c> beside <c>enshrouded.exe</c>
/// that, on every launch, rebuilds <c>enshrouded.kfc</c> from its backup and applies the
/// mods in <c>mods/</c>. A mod is a folder holding a <c>mod.json</c> manifest and its
/// Lua sources; EML expects it at <c>mods/&lt;id&gt;/</c>.
/// </para>
/// <para>
/// So an archive is one of two things: mod folders, found by their <c>mod.json</c>, each
/// routed to <c>mods/&lt;id&gt;/</c> whatever wraps it in the archive; or a loader, a
/// proxy DLL and its companions, routed to the game root.
/// </para>
/// </remarks>
public static partial class EnshroudedArchiveAnalyzer
{
    /// <summary>The folder EML reads mods from, relative to the game root.</summary>
    public const string ModsFolder = "mods";

    /// <summary>The manifest that makes a folder a mod.</summary>
    public const string ManifestName = "mod.json";

    /// <summary>
    /// Proxy DLL names Enshrouded loaders and native mods install beside the executable:
    /// EML <c>dbghelp.dll</c> (or <c>dinput8.dll</c>); ShroudForge <c>winmm.dll</c>;
    /// Greeneye <c>dbgcore.dll</c>; Building Tools <c>dinput8.dll</c>; Shrouded Engine
    /// Tweaks and the plugin loader shipped with Combat Rebalanced <c>powrprof.dll</c>.
    /// </summary>
    public static readonly IReadOnlySet<string> LoaderDllNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "dbghelp.dll", "dinput8.dll", "winmm.dll", "dbgcore.dll", "powrprof.dll", "version.dll"
        };

    /// <summary>
    /// Shrouded Engine Tweaks' folder. SET is itself a <c>powrprof.dll</c>, and loads any
    /// other mod's <c>powrprof.dll</c> placed, under any name, in its <c>plugins</c> folder.
    /// </summary>
    public const string SetFolder = "shrouded_engine_tweaks";

    /// <summary>Files that are only for people reading the archive, never installed.</summary>
    private static readonly HashSet<string> DocumentExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".pdf", ".url", ".html", ".htm", ".png", ".jpg", ".jpeg", ".webp", ".gif" };

    private static readonly JsonDocumentOptions ManifestJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>
    /// Classifies an archive.
    /// </summary>
    /// <param name="entries">The archive's entries.</param>
    /// <param name="readText">
    /// Reads an entry's text, for the manifests; returns <c>null</c> when it cannot.
    /// </param>
    /// <param name="archiveName">The archive's file name, used to name a mod whose manifest has no usable id.</param>
    /// <param name="gameDirectory">
    /// The game folder, when known. With Shrouded Engine Tweaks in it, another mod's
    /// <c>powrprof.dll</c> is routed into SET's plugins folder instead of replacing SET.
    /// </param>
    /// <returns>The layout.</returns>
    public static EnshroudedInstallLayout Analyze(
        IReadOnlyList<ArchiveEntry> entries,
        Func<ArchiveEntry, string?> readText,
        string? archiveName = null,
        string? gameDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(readText);

        var files = entries
            .Where(e => !e.IsDirectory && !IsJunk(Normalize(e.FullName)))
            .ToList();

        if (files.Count == 0)
            return EnshroudedInstallLayout.Unknown("Archive is empty");

        if (files.Any(f => IsStandalonePatcher(Path.GetFileName(Normalize(f.FullName)))))
        {
            return EnshroudedInstallLayout.Unknown(
                "This is the standalone Enshrouded Patcher, a program that rewrites " +
                "enshrouded.kfc itself. Run it yourself; it is not a mod Modular can deploy.");
        }

        // A mod root is the folder holding a mod.json. A mod.json inside another mod's
        // folder belongs to that mod (examples, templates), so only the shallowest count.
        var manifestDirs = files
            .Select(f => Normalize(f.FullName))
            .Where(p => string.Equals(FileName(p), ManifestName, StringComparison.OrdinalIgnoreCase))
            .Select(DirectoryOf)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d.Length)
            .ToList();

        var roots = new List<string>();
        foreach (var dir in manifestDirs)
        {
            if (!roots.Any(r => IsUnder(dir, r)))
                roots.Add(dir);
        }

        var layout = new EnshroudedInstallLayout();
        var folderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            var manifestEntry = files.First(f =>
                string.Equals(Normalize(f.FullName), Join(root, ManifestName), StringComparison.OrdinalIgnoreCase));

            var manifest = ParseManifest(readText(manifestEntry));
            var folder = ChooseFolderName(manifest, root, archiveName);

            if (folder == null)
            {
                return EnshroudedInstallLayout.Unknown(
                    $"{Join(root, ManifestName)} has no usable id, and the archive gives no " +
                    "other name for the mod's folder.");
            }

            if (!folderNames.Add(folder))
            {
                return EnshroudedInstallLayout.Unknown(
                    $"Two mods in this archive would both install to {ModsFolder}/{folder}.");
            }

            layout.Mods.Add(new EnshroudedModInfo(
                folder,
                manifest?.Id,
                manifest?.Name,
                manifest?.Version,
                manifest?.Capabilities ?? Array.Empty<string>(),
                root));
        }

        var outside = new List<ArchiveEntry>();

        foreach (var entry in files)
        {
            var path = Normalize(entry.FullName);
            var mod = layout.Mods.FirstOrDefault(m => IsUnder(DirectoryOf(path), m.ArchiveRoot));

            if (mod == null)
            {
                outside.Add(entry);
                continue;
            }

            var relative = mod.ArchiveRoot.Length == 0 ? path : path[(mod.ArchiveRoot.Length + 1)..];
            layout.FileRoutes[entry.FullName] = FileRoute.Declared(
                $"{ModsFolder}/{mod.Folder}/{relative}", PackageTypes.EnshroudedMod);
        }

        // Everything outside the mod folders: a loader goes to the game root, keeping the
        // archive's layout below any single wrapper folder; anything else is unexplained.
        var hasLoader = outside.Any(e => LoaderDllNames.Contains(FileName(Normalize(e.FullName))));

        if (hasLoader)
        {
            var wrapper = CommonWrapper(outside.Select(e => Normalize(e.FullName)).ToList());
            var relatives = outside.ToDictionary(
                e => e.FullName,
                e => wrapper.Length == 0 ? Normalize(e.FullName) : Normalize(e.FullName)[(wrapper.Length + 1)..]);

            var hostedBySet = ShouldHostInSet(relatives.Values, gameDirectory);

            foreach (var entry in outside)
            {
                var relative = relatives[entry.FullName];

                if (hostedBySet && string.Equals(relative, "powrprof.dll", StringComparison.OrdinalIgnoreCase))
                {
                    relative = $"{SetFolder}/plugins/{PluginName(archiveName)}-powrprof.dll";
                    layout.HostedInSet = true;
                }

                layout.FileRoutes[entry.FullName] = FileRoute.Declared(relative, PackageTypes.EnshroudedLoader);
                layout.LoaderFiles.Add(relative);
            }
        }
        else
        {
            foreach (var entry in outside)
                layout.SkippedFiles.Add(Normalize(entry.FullName));
        }

        if (layout.Mods.Count == 0 && !hasLoader)
        {
            return EnshroudedInstallLayout.Unknown(
                "No mod.json and no loader DLL: this is not an EML mod or loader.");
        }

        // Files Modular cannot place, other than documentation, mean the archive is
        // something else as well; say so rather than drop them quietly.
        var unexplained = layout.SkippedFiles
            .Where(p => !DocumentExtensions.Contains(Path.GetExtension(p)))
            .ToList();

        layout.Confidence = unexplained.Count == 0 ? 0.95 : 0.6;
        layout.Reason = Describe(layout, unexplained);

        return layout;
    }

    /// <summary>
    /// Whether a mod id is one EML accepts: lower-case letters, digits, <c>-</c> and
    /// <c>_</c>, starting with a letter. ShroudForge also allows dots.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>True when the id is usable as a folder name and accepted by the loaders.</returns>
    public static bool IsValidModId(string? id) =>
        !string.IsNullOrEmpty(id) && ModIdPattern().IsMatch(id);

    /// <summary>
    /// Reads the fields of a <c>mod.json</c> Modular uses.
    /// </summary>
    /// <param name="json">The manifest text.</param>
    /// <returns>The manifest, or <c>null</c> when it is not a JSON object.</returns>
    public static EnshroudedManifest? ParseManifest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json, ManifestJsonOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var root = doc.RootElement;

            var capabilities = root.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array
                ? caps.EnumerateArray()
                    .Where(c => c.ValueKind == JsonValueKind.String)
                    .Select(c => c.GetString()!)
                    .ToList()
                : new List<string>();

            var dependencies = new List<string>();
            if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Array)
            {
                foreach (var dep in deps.EnumerateArray())
                {
                    if (dep.ValueKind == JsonValueKind.String)
                    {
                        dependencies.Add(dep.GetString()!);
                    }
                    else if (dep.ValueKind == JsonValueKind.Object &&
                             dep.TryGetProperty("id", out var depId) &&
                             depId.ValueKind == JsonValueKind.String &&
                             !(dep.TryGetProperty("optional", out var optional) && optional.ValueKind == JsonValueKind.True))
                    {
                        dependencies.Add(depId.GetString()!);
                    }
                }
            }

            return new EnshroudedManifest(
                StringProperty(root, "id"),
                StringProperty(root, "name"),
                StringProperty(root, "version"),
                capabilities,
                dependencies);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The capabilities EML's manifest accepts; any other value fails to load.</summary>
    public static readonly IReadOnlySet<string> EmlCapabilities =
        new HashSet<string>(StringComparer.Ordinal) { "export", "patch", "runtime", "runtime-register-dll" };

    /// <summary>
    /// Finds what would make EML reject a <c>mod.json</c>.
    /// </summary>
    /// <param name="json">The manifest text.</param>
    /// <returns>The problems; empty when EML will load it.</returns>
    /// <remarks>
    /// EML reads manifests with a strict JSON parser into a fixed shape (id, name and a
    /// semantic version required; capabilities from a closed set; dependencies as
    /// objects), and one manifest it cannot read stops it loading every mod. So this is
    /// stricter than <see cref="ParseManifest"/>, which only reads what Modular uses.
    /// </remarks>
    public static IReadOnlyList<string> FindEmlManifestProblems(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return [$"mod.json is not strict JSON ({ex.Message})"];
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ["mod.json is not a JSON object"];

            var problems = new List<string>();

            foreach (var field in new[] { "id", "name", "version" })
            {
                if (!root.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
                    problems.Add($"mod.json has no \"{field}\" string");
            }

            if (root.TryGetProperty("version", out var version) &&
                version.ValueKind == JsonValueKind.String &&
                !SemVerPattern().IsMatch(version.GetString()!))
            {
                problems.Add($"version \"{version.GetString()}\" is not a semantic version such as 1.0.0");
            }

            if (root.TryGetProperty("capabilities", out var caps))
            {
                if (caps.ValueKind != JsonValueKind.Array)
                {
                    problems.Add("\"capabilities\" is not a list");
                }
                else
                {
                    foreach (var cap in caps.EnumerateArray())
                    {
                        if (cap.ValueKind != JsonValueKind.String || !EmlCapabilities.Contains(cap.GetString()!))
                            problems.Add($"unknown capability {cap.GetRawText()}");
                    }
                }
            }

            if (root.TryGetProperty("dependencies", out var deps))
            {
                if (deps.ValueKind != JsonValueKind.Array)
                {
                    problems.Add("\"dependencies\" is not a list");
                }
                else if (deps.EnumerateArray().Any(d =>
                             d.ValueKind != JsonValueKind.Object ||
                             !d.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                             !d.TryGetProperty("version", out var req) || req.ValueKind != JsonValueKind.String))
                {
                    problems.Add("each dependency must be an object with \"id\" and \"version\" strings");
                }
            }

            return problems;
        }
    }

    private static string? StringProperty(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ChooseFolderName(EnshroudedManifest? manifest, string root, string? archiveName)
    {
        if (IsValidModId(manifest?.Id))
            return manifest!.Id;

        var lastSegment = root.Length == 0 ? null : root[(root.LastIndexOf('/') + 1)..];
        if (!string.IsNullOrWhiteSpace(lastSegment) &&
            !string.Equals(lastSegment, ModsFolder, StringComparison.OrdinalIgnoreCase))
        {
            return lastSegment;
        }

        if (!string.IsNullOrWhiteSpace(archiveName))
        {
            var stem = Path.GetFileNameWithoutExtension(archiveName);
            if (!string.IsNullOrWhiteSpace(stem))
                return stem;
        }

        return null;
    }

    private static string Describe(EnshroudedInstallLayout layout, IReadOnlyList<string> unexplained)
    {
        var parts = new List<string>();

        if (layout.Mods.Count > 0)
        {
            parts.Add(layout.Mods.Count == 1
                ? $"EML mod '{layout.Mods[0].Name ?? layout.Mods[0].Folder}' → {ModsFolder}/{layout.Mods[0].Folder}"
                : $"{layout.Mods.Count} EML mods → {ModsFolder}/");
        }

        if (layout.LoaderFiles.Count > 0)
            parts.Add($"loader files → game root ({string.Join(", ", layout.LoaderFiles)})");

        if (layout.HostedInSet)
            parts.Add("its powrprof.dll loads through Shrouded Engine Tweaks' plugins folder");

        if (unexplained.Count > 0)
            parts.Add($"{unexplained.Count} file(s) outside any mod folder are not installed");

        return string.Join("; ", parts);
    }

    /// <summary>
    /// Whether this archive's root <c>powrprof.dll</c> should load through Shrouded Engine
    /// Tweaks: SET is in the game folder and this archive is not SET itself.
    /// </summary>
    private static bool ShouldHostInSet(IEnumerable<string> relatives, string? gameDirectory)
    {
        var list = relatives.ToList();

        if (!list.Any(r => string.Equals(r, "powrprof.dll", StringComparison.OrdinalIgnoreCase)))
            return false;

        if (list.Any(r => r.StartsWith(SetFolder + "/", StringComparison.OrdinalIgnoreCase)))
            return false;

        return !string.IsNullOrEmpty(gameDirectory) && Directory.Exists(Path.Combine(gameDirectory, SetFolder));
    }

    private static string PluginName(string? archiveName)
    {
        var stem = Path.GetFileNameWithoutExtension(archiveName ?? "mod");
        var clean = new string(stem.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        return clean.Length == 0 ? "mod" : clean;
    }

    /// <summary>The single top-level folder every path shares, or empty when there is none.</summary>
    private static string CommonWrapper(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || paths.Any(p => !p.Contains('/')))
            return string.Empty;

        var first = paths[0][..paths[0].IndexOf('/')];

        return paths.All(p => p.StartsWith(first + "/", StringComparison.OrdinalIgnoreCase))
            ? first
            : string.Empty;
    }

    private static bool IsStandalonePatcher(string fileName) =>
        fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        fileName.Replace('_', '-').StartsWith("enshrouded-patcher", StringComparison.OrdinalIgnoreCase);

    private static bool IsJunk(string path) =>
        path.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) ||
        FileName(path).Equals(".DS_Store", StringComparison.OrdinalIgnoreCase) ||
        FileName(path).Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string dir, string root) =>
        root.Length == 0 ||
        string.Equals(dir, root, StringComparison.OrdinalIgnoreCase) ||
        dir.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static string DirectoryOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    private static string Join(string dir, string name) => dir.Length == 0 ? name : dir + "/" + name;

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9._-]*$")]
    private static partial Regex ModIdPattern();

    [GeneratedRegex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex SemVerPattern();
}

/// <summary>The fields of an EML <c>mod.json</c> Modular reads.</summary>
/// <param name="Id">The mod id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Version">Semantic version.</param>
/// <param name="Capabilities">What the mod does: <c>patch</c>, <c>export</c>, <c>runtime</c>.</param>
/// <param name="Dependencies">Ids of the mods it requires, optional ones left out.</param>
public sealed record EnshroudedManifest(
    string? Id,
    string? Name,
    string? Version,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> Dependencies);

/// <summary>One mod found in an archive.</summary>
/// <param name="Folder">The folder it installs to, under <c>mods/</c>.</param>
/// <param name="Id">The manifest's id.</param>
/// <param name="Name">The manifest's name.</param>
/// <param name="Version">The manifest's version.</param>
/// <param name="Capabilities">The manifest's capabilities.</param>
/// <param name="ArchiveRoot">Where its folder is in the archive, empty for the archive root.</param>
public sealed record EnshroudedModInfo(
    string Folder,
    string? Id,
    string? Name,
    string? Version,
    IReadOnlyList<string> Capabilities,
    string ArchiveRoot);

/// <summary>The result of <see cref="EnshroudedArchiveAnalyzer.Analyze"/>.</summary>
public sealed class EnshroudedInstallLayout
{
    /// <summary>Whether the archive is something this installer handles.</summary>
    public bool CanHandle => Mods.Count > 0 || LoaderFiles.Count > 0;

    /// <summary>How sure the analysis is.</summary>
    public double Confidence { get; set; }

    /// <summary>What was found, or why the archive is not handled.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>The mods found.</summary>
    public List<EnshroudedModInfo> Mods { get; } = new();

    /// <summary>Loader files, relative to the game root.</summary>
    public List<string> LoaderFiles { get; } = new();

    /// <summary>Whether a <c>powrprof.dll</c> was routed into Shrouded Engine Tweaks' plugins folder.</summary>
    public bool HostedInSet { get; set; }

    /// <summary>Archive files that are not installed.</summary>
    public List<string> SkippedFiles { get; } = new();

    /// <summary>Destination for each installed entry, keyed by the entry's full name.</summary>
    public Dictionary<string, FileRoute> FileRoutes { get; } = new(StringComparer.Ordinal);

    /// <summary>A layout for an archive this installer does not handle.</summary>
    /// <param name="reason">Why.</param>
    /// <returns>The layout.</returns>
    public static EnshroudedInstallLayout Unknown(string reason) => new() { Reason = reason };
}
