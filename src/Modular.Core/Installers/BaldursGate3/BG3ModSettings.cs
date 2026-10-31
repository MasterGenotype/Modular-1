using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Modular.Core.Installers.BaldursGate3;

/// <summary>
/// A pak Modular deployed, as the load order needs to know it.
/// </summary>
/// <param name="Module">The module the pak contains.</param>
/// <param name="Tier">The band the pak belongs in.</param>
/// <param name="OrderIndex">Modular's own position for the mod, breaking ties in a band.</param>
public sealed record BG3ManagedPak(BG3ModuleRef Module, BG3LoadTier Tier, int OrderIndex);

/// <summary>
/// What an update to <c>modsettings.lsx</c> did.
/// </summary>
public sealed record BG3ModSettingsResult
{
    /// <summary>Whether the file was written. False when it was already as it should be.</summary>
    public bool Written { get; init; }

    /// <summary>Names of the modules in the load order, first loaded to last.</summary>
    public IReadOnlyList<string> Order { get; init; } = [];

    /// <summary>Names of modules added to the order.</summary>
    public IReadOnlyList<string> Added { get; init; } = [];

    /// <summary>Names of modules removed from the order.</summary>
    public IReadOnlyList<string> Removed { get; init; } = [];

    /// <summary>Names of modules moved so that they load after what they depend on.</summary>
    public IReadOnlyList<string> Moved { get; init; } = [];

    /// <summary>Names of listed modules whose version was brought up to their pak's.</summary>
    public IReadOnlyList<string> Refreshed { get; init; } = [];

    /// <summary>Things that could not be put right, as messages fit to show.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Maintains <c>modsettings.lsx</c>, the file that tells Baldur's Gate 3 which mods to
/// load and in what order.
/// </summary>
/// <remarks>
/// <para>
/// The list in that file is the load order: position is everything, file names are
/// nothing. It is also shared. The game's own mod manager writes it, and so does any
/// other mod manager in use, so Modular edits it rather than owning it.
/// </para>
/// <para>
/// The rules are deliberately conservative. An entry Modular did not add is never
/// removed and keeps its position. A new mod of Modular's goes in by band and then by
/// Modular's order, just above the first of its mods that belongs after it, and above
/// anything already listed that depends on it. An entry already in the list, Modular's
/// or not, is moved only when the list as found had it above something it depends on,
/// so an order the user set by hand, or restored from a save, survives every
/// deployment. Which entries are Modular's is
/// remembered in a small file beside <c>modsettings.lsx</c>, so that uninstalling a mod
/// removes its entry and nobody else's.
/// </para>
/// </remarks>
public static class BG3ModSettings
{
    /// <summary>File name of the load-order file.</summary>
    public const string FileName = "modsettings.lsx";

    /// <summary>The profile folder holding the file, relative to the game's user data folder.</summary>
    public const string ProfileFolder = "PlayerProfiles/Public";

    /// <summary>UUID of the base campaign, which is always first in the order.</summary>
    public const string GustavXUuid = "cb555efe-2d9e-131f-8195-a89329d218ea";

    private const string OwnershipFileName = "modsettings.modular.json";
    private const string BackupSuffix = ".modular.bak";
    private const string OriginalSuffix = ".modular.orig";

    private static readonly BG3ModuleRef GustavX =
        new(GustavXUuid, "GustavX", "GustavX", 0, string.Empty, BG3ModuleRef.DefaultVersion64);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Brings the load order in line with what is installed.
    /// </summary>
    /// <param name="modSettingsPath">Absolute path of <c>modsettings.lsx</c>.</param>
    /// <param name="installed">
    /// Every module with a pak in the user <c>Mods</c> folder, whoever put it there. Used
    /// to tell an uninstalled mod from a present one, and for dependencies.
    /// </param>
    /// <param name="managed">The paks Modular currently has deployed.</param>
    /// <param name="mayWrite">
    /// Asked only when the file needs changing; returning false refuses the write. Lets a
    /// caller hold off while the game is running without blocking a no-op.
    /// </param>
    /// <returns>What changed.</returns>
    /// <exception cref="InvalidOperationException">
    /// A change was needed and <paramref name="mayWrite"/> refused it.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The existing file is not a <c>modsettings.lsx</c> this code understands. It is left
    /// untouched: guessing would risk the user's whole load order.
    /// </exception>
    public static BG3ModSettingsResult Update(
        string modSettingsPath,
        IReadOnlyList<BG3PakMeta> installed,
        IReadOnlyList<BG3ManagedPak> managed,
        Func<bool>? mayWrite = null)
    {
        ArgumentNullException.ThrowIfNull(modSettingsPath);
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(managed);

        var exists = File.Exists(modSettingsPath);
        var originalText = exists ? File.ReadAllText(modSettingsPath) : null;
        var document = originalText != null ? Parse(originalText) : CreateEmpty();
        var modsChildren = FindModsChildren(document);

        var entries = modsChildren.Elements("node")
            .Where(n => HasId(n, "ModuleShortDesc"))
            .Select(n => new Entry(ReadAttribute(n, "UUID").ToLowerInvariant(), ReadAttribute(n, "Name"), n))
            .Where(e => e.Uuid.Length > 0)
            .DistinctBy(e => e.Uuid)
            .ToList();

        var before = entries.Select(e => e.Uuid).ToList();
        var installedByUuid = installed
            .GroupBy(m => m.Module.Uuid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var dependsOn = DependencyMap(installedByUuid);

        var ownershipPath = Path.Combine(
            Path.GetDirectoryName(modSettingsPath) ?? string.Empty, OwnershipFileName);
        var previouslyOwned = ReadOwnership(ownershipPath);
        var managedUuids = managed
            .Select(m => m.Module.Uuid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // An entry goes only if Modular added it, no longer deploys it, and its pak is
        // really gone. Anything else in the list is someone else's to remove.
        var removed = entries
            .Where(e => previouslyOwned.Contains(e.Uuid) &&
                        !managedUuids.Contains(e.Uuid) &&
                        !installedByUuid.ContainsKey(e.Uuid))
            .ToList();
        entries.RemoveAll(removed.Contains);

        // The base campaign leads the list; the game expects to find it there.
        var gustav = entries.FirstOrDefault(e => e.Uuid == GustavXUuid);
        if (gustav == null)
            entries.Insert(0, new Entry(GustavXUuid, GustavX.Name, CreateNode(GustavX)));
        else if (entries[0] != gustav)
        {
            entries.Remove(gustav);
            entries.Insert(0, gustav);
        }

        var present = entries.Select(e => e.Uuid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<Entry>();
        var managedByUuid = managed
            .GroupBy(m => m.Module.Uuid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // A reinstall can change a listed mod's version. The entry keeps its place, but
        // says what the deployed pak now is.
        var refreshed = RefreshVersions(entries, managedByUuid);

        foreach (var pak in managed
                     .OrderBy(m => m.Tier.Rank)
                     .ThenBy(m => m.OrderIndex)
                     .ThenBy(m => m.Module.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!present.Add(pak.Module.Uuid))
                continue;

            var entry = new Entry(pak.Module.Uuid, pak.Module.Name, CreateNode(pak.Module));

            // A new mod goes in by band, just above the first of Modular's mods that
            // belongs after it, and above anything already listed that depends on it.
            // Nothing already in the list moves to make room: its order may be the
            // user's own, or the one a save was made with.
            var firstLater = entries.FindIndex(1, e =>
                managedByUuid.TryGetValue(e.Uuid, out var other) && LoadsAfter(other, pak));

            var firstDependent = entries.FindIndex(1, e =>
                dependsOn.TryGetValue(e.Uuid, out var needs) && needs.Contains(pak.Module.Uuid));

            var at = new[] { firstLater, firstDependent }.Where(i => i > 0).DefaultIfEmpty(-1).Min();

            if (at > 0)
                entries.Insert(at, entry);
            else
                entries.Add(entry);

            added.Add(entry);
        }

        var mine = previouslyOwned
            .Where(managedUuids.Contains)
            .Concat(added.Select(e => e.Uuid))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var warnings = new List<string>();
        var moved = PutDependenciesFirst(entries, dependsOn, warnings);

        // Only for Modular's own mods: a gap in someone else's is theirs to report, and
        // would otherwise be repeated on every deployment.
        foreach (var entry in entries.Where(e => managedUuids.Contains(e.Uuid)))
        {
            if (!installedByUuid.TryGetValue(entry.Uuid, out var meta))
                continue;

            foreach (var dependency in meta.ModDependencies.Where(d => !present.Contains(d.Uuid)))
            {
                warnings.Add(
                    $"'{entry.Name}' needs '{Display(dependency)}', which is not in the load order.");
            }
        }

        var after = entries.Select(e => e.Uuid).ToList();
        var result = new BG3ModSettingsResult
        {
            Order = entries.Select(e => e.Name).ToList(),
            Added = added.Select(e => e.Name).ToList(),
            Removed = removed.Select(e => e.Name).ToList(),
            Moved = moved,
            Refreshed = refreshed,
            Warnings = warnings
        };

        // Only entries Modular added count as its own. A mod it deploys that the game had
        // already listed stays the game's entry, to be neither moved nor removed.
        var owned = mine.Order(StringComparer.Ordinal).ToList();

        if (exists && before.SequenceEqual(after) && refreshed.Count == 0)
        {
            WriteOwnership(ownershipPath, owned, previouslyOwned);
            return result;
        }

        if (mayWrite != null && !mayWrite())
        {
            throw new InvalidOperationException(
                $"{FileName} needs updating but may not be written right now");
        }

        Write(modSettingsPath, document, modsChildren, entries, originalText);
        WriteOwnership(ownershipPath, owned, previouslyOwned);

        return result with { Written = true };
    }

    /// <summary>
    /// Puts the load order's entries in a new order the user chose.
    /// </summary>
    /// <param name="modSettingsPath">Absolute path of <c>modsettings.lsx</c>.</param>
    /// <param name="uuidsInOrder">
    /// Every entry's UUID, first loaded to last. It must be a reordering of the list as it
    /// stands: adding and removing entries stays <see cref="Update"/>'s job.
    /// </param>
    /// <param name="installed">
    /// Every module with a pak in the user <c>Mods</c> folder, for their dependencies.
    /// </param>
    /// <param name="mayWrite">
    /// Asked only when the order actually changes; returning false refuses the write.
    /// </param>
    /// <returns>Whether the file was written.</returns>
    /// <exception cref="InvalidOperationException">
    /// The order cannot be used as given: it is not a reordering of the list, it moves the
    /// base campaign off the top, it loads a mod before one it depends on, or
    /// <paramref name="mayWrite"/> refused. The message says which, and nothing is written.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The existing file is not a <c>modsettings.lsx</c> this code understands.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The order is refused rather than repaired when it puts a mod above something it
    /// depends on. <see cref="Update"/> moves such a mod back below its dependency on the
    /// next deployment, so accepting the order would only undo it later, silently.
    /// </para>
    /// <para>
    /// Every entry keeps its own node, so nothing the game or another tool recorded in it
    /// is lost; only the order changes. The ownership record is untouched.
    /// </para>
    /// </remarks>
    public static bool SetOrder(
        string modSettingsPath,
        IReadOnlyList<string> uuidsInOrder,
        IReadOnlyList<BG3PakMeta> installed,
        Func<bool>? mayWrite = null)
    {
        ArgumentNullException.ThrowIfNull(modSettingsPath);
        ArgumentNullException.ThrowIfNull(uuidsInOrder);
        ArgumentNullException.ThrowIfNull(installed);

        if (!File.Exists(modSettingsPath))
            throw new InvalidOperationException($"{FileName} does not exist yet, so there is no order to change.");

        var originalText = File.ReadAllText(modSettingsPath);
        var document = Parse(originalText);
        var modsChildren = FindModsChildren(document);

        var entries = modsChildren.Elements("node")
            .Where(n => HasId(n, "ModuleShortDesc"))
            .Select(n => new Entry(ReadAttribute(n, "UUID").ToLowerInvariant(), ReadAttribute(n, "Name"), n))
            .Where(e => e.Uuid.Length > 0)
            .DistinctBy(e => e.Uuid)
            .ToList();

        var byUuid = entries.ToDictionary(e => e.Uuid, StringComparer.OrdinalIgnoreCase);
        var requested = uuidsInOrder.Select(u => u.ToLowerInvariant()).ToList();

        var unknown = requested.Where(u => !byUuid.ContainsKey(u)).ToList();
        var missing = entries.Where(e => !requested.Contains(e.Uuid)).Select(e => e.Name).ToList();

        if (unknown.Count > 0 || missing.Count > 0 || requested.Distinct().Count() != requested.Count)
        {
            throw new InvalidOperationException(
                $"The new order is not a reordering of {FileName} as it stands" +
                (missing.Count > 0 ? $"; it leaves out {string.Join(", ", missing.Select(n => $"'{n}'"))}" : string.Empty) +
                (unknown.Count > 0 ? $"; {unknown.Count} of its entries are not in the file" : string.Empty) +
                ". Refresh and try again.");
        }

        if (entries.Count > 0 && entries[0].Uuid == GustavXUuid && requested[0] != GustavXUuid)
            throw new InvalidOperationException("GustavX, the base campaign, has to stay first in the load order.");

        var ordered = requested.Select(u => byUuid[u]).ToList();

        var installedByUuid = installed
            .GroupBy(m => m.Module.Uuid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var dependsOn = DependencyMap(installedByUuid);
        var position = ordered
            .Select((e, i) => (e.Uuid, i))
            .ToDictionary(x => x.Uuid, x => x.i, StringComparer.OrdinalIgnoreCase);

        var violations = new List<string>();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (!dependsOn.TryGetValue(ordered[i].Uuid, out var needs))
                continue;

            foreach (var need in needs)
            {
                if (position.TryGetValue(need, out var at) && at > i)
                    violations.Add($"'{ordered[i].Name}' needs '{ordered[at].Name}' loaded before it");
            }
        }

        if (violations.Count > 0)
        {
            throw new InvalidOperationException(
                "That order would load a mod before one it depends on: " +
                string.Join("; ", violations) + ".");
        }

        if (ordered.Select(e => e.Uuid).SequenceEqual(entries.Select(e => e.Uuid)))
            return false;

        if (mayWrite != null && !mayWrite())
            throw new InvalidOperationException($"{FileName} may not be written right now");

        Write(modSettingsPath, document, modsChildren, ordered, originalText);
        return true;
    }

    /// <summary>
    /// Reads the load order as it stands, with each entry's UUID.
    /// </summary>
    /// <param name="modSettingsPath">Absolute path of <c>modsettings.lsx</c>.</param>
    /// <returns>
    /// The entries, first loaded to last; empty when the file is missing or unreadable.
    /// </returns>
    public static IReadOnlyList<(string Uuid, string Name)> ReadEntries(string modSettingsPath)
    {
        try
        {
            if (!File.Exists(modSettingsPath))
                return [];

            return FindModsChildren(Parse(File.ReadAllText(modSettingsPath)))
                .Elements("node")
                .Where(n => HasId(n, "ModuleShortDesc"))
                .Select(n => (Uuid: ReadAttribute(n, "UUID").ToLowerInvariant(), Name: ReadAttribute(n, "Name")))
                .Where(e => e.Uuid.Length > 0)
                .DistinctBy(e => e.Uuid)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return [];
        }
    }

    /// <summary>
    /// Whether Modular has ever added an entry to the load order beside this path.
    /// </summary>
    /// <param name="modSettingsPath">Absolute path of <c>modsettings.lsx</c>.</param>
    public static bool HasOwnershipRecord(string modSettingsPath) =>
        File.Exists(Path.Combine(
            Path.GetDirectoryName(modSettingsPath) ?? string.Empty, OwnershipFileName));

    /// <summary>
    /// Reads the load order as it stands.
    /// </summary>
    /// <param name="modSettingsPath">Absolute path of <c>modsettings.lsx</c>.</param>
    /// <returns>
    /// Module names, first loaded to last; empty when the file is missing or unreadable.
    /// </returns>
    public static IReadOnlyList<string> ReadOrder(string modSettingsPath)
    {
        try
        {
            if (!File.Exists(modSettingsPath))
                return [];

            return FindModsChildren(Parse(File.ReadAllText(modSettingsPath)))
                .Elements("node")
                .Where(n => HasId(n, "ModuleShortDesc"))
                .Select(n => ReadAttribute(n, "Name"))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return [];
        }
    }

    // Band first, then Modular's own order: where a new mod goes among those listed.
    private static bool LoadsAfter(BG3ManagedPak listed, BG3ManagedPak pak) =>
        listed.Tier.Rank != pak.Tier.Rank
            ? listed.Tier.Rank > pak.Tier.Rank
            : listed.OrderIndex > pak.OrderIndex;

    // Sets each deployed mod's listed version to its pak's. Nothing else in an entry is
    // touched: the game keeps the hash and handle up to date itself.
    private static List<string> RefreshVersions(
        List<Entry> entries,
        Dictionary<string, BG3ManagedPak> managedByUuid)
    {
        var refreshed = new List<string>();

        foreach (var entry in entries)
        {
            // The default is what a pak gets when its version could not be read, such as
            // an old meta with a 64-bit value in Version. The game reads those its own
            // way, so the version it listed is kept rather than replaced with a guess.
            if (!managedByUuid.TryGetValue(entry.Uuid, out var pak) ||
                pak.Module.Version64 == BG3ModuleRef.DefaultVersion64)
            {
                continue;
            }

            var version = pak.Module.Version64.ToString(CultureInfo.InvariantCulture);
            var attribute = entry.Node.Elements("attribute").FirstOrDefault(a => HasId(a, "Version64"));

            if (attribute == null || (string?)attribute.Attribute("value") == version)
                continue;

            attribute.SetAttributeValue("value", version);
            refreshed.Add(entry.Name);
        }

        return refreshed;
    }

    // What each installed mod needs loaded before it: the mods its meta.lsx declares, and
    // the mods whose script tables its scripts use while loading. A script reference that
    // points back at a mod depending on this one is dropped rather than made into a loop:
    // it is the less certain of the two.
    private static Dictionary<string, HashSet<string>> DependencyMap(
        Dictionary<string, BG3PakMeta> installedByUuid)
    {
        var declared = installedByUuid.ToDictionary(
            p => p.Key,
            p => p.Value.ModDependencies.Select(d => d.Uuid).ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        var byTable = installedByUuid
            .Where(p => p.Value.ScriptModTable != null)
            .GroupBy(p => p.Value.ScriptModTable!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Key).ToList(), StringComparer.Ordinal);

        var scripted = installedByUuid.ToDictionary(
            p => p.Key,
            p => p.Value.ScriptReferences
                .SelectMany(t => byTable.GetValueOrDefault(t) ?? [])
                .Where(d => !string.Equals(d, p.Key, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        foreach (var (uuid, needs) in scripted)
        {
            needs.RemoveWhere(d =>
                declared.GetValueOrDefault(d)?.Contains(uuid) == true ||
                scripted.GetValueOrDefault(d)?.Contains(uuid) == true);
        }

        foreach (var (uuid, needs) in declared)
            needs.UnionWith(scripted[uuid]);

        return declared;
    }

    // Moves any mod that sits above something it depends on to just below it. Nothing
    // else moves, so an order that was already valid comes back exactly as it went in.
    private static List<string> PutDependenciesFirst(
        List<Entry> entries,
        Dictionary<string, HashSet<string>> dependsOn,
        List<string> warnings)
    {
        var moved = new List<string>();

        // Each pass fixes at least one mod for good unless dependencies form a loop, so a
        // loop is the only way to run out of passes.
        var passesLeft = entries.Count * entries.Count + 1;

        for (var i = 1; i < entries.Count; i++)
        {
            if (!dependsOn.TryGetValue(entries[i].Uuid, out var needs))
                continue;

            var last = needs
                .Select(d => entries.FindIndex(e => string.Equals(e.Uuid, d, StringComparison.OrdinalIgnoreCase)))
                .DefaultIfEmpty(-1)
                .Max();

            if (last <= i)
                continue;

            if (--passesLeft <= 0)
            {
                warnings.Add(
                    $"'{entries[i].Name}' is part of a dependency loop, so its position was left alone.");
                break;
            }

            var entry = entries[i];
            entries.RemoveAt(i);
            entries.Insert(last, entry);

            if (!moved.Contains(entry.Name))
                moved.Add(entry.Name);

            // Whatever slid into this slot has not been looked at yet.
            i--;
        }

        return moved;
    }

    private static XDocument Parse(string text)
    {
        try
        {
            return XDocument.Parse(text);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"{FileName} is not valid XML: {ex.Message}", ex);
        }
    }

    private static XDocument CreateEmpty() =>
        new(new XElement("save",
            new XElement("version",
                new XAttribute("major", "4"),
                new XAttribute("minor", "8"),
                new XAttribute("revision", "0"),
                new XAttribute("build", "100")),
            new XElement("region",
                new XAttribute("id", "ModuleSettings"),
                new XElement("node",
                    new XAttribute("id", "root"),
                    new XElement("children",
                        new XElement("node",
                            new XAttribute("id", "Mods"),
                            new XElement("children")))))));

    private static XElement FindModsChildren(XDocument document)
    {
        var mods = document.Descendants("node").FirstOrDefault(n => HasId(n, "Mods"))
            ?? throw new InvalidDataException($"{FileName} has no Mods list.");

        var children = mods.Element("children");
        if (children == null)
        {
            children = new XElement("children");
            mods.Add(children);
        }

        return children;
    }

    // Game versions before the single Mods list kept a second list, ModOrder, holding
    // just the UUIDs. When a file has one it is kept in step; it is never introduced.
    private static void MirrorModOrder(XDocument document, IReadOnlyList<string> uuids)
    {
        var modOrder = document.Descendants("node").FirstOrDefault(n => HasId(n, "ModOrder"));
        if (modOrder == null)
            return;

        var children = modOrder.Element("children");
        if (children == null)
        {
            children = new XElement("children");
            modOrder.Add(children);
        }

        children.ReplaceNodes(uuids.Select(uuid =>
            new XElement("node",
                new XAttribute("id", "Module"),
                Attribute("UUID", "FixedString", uuid))));
    }

    // The attribute set and order the game itself writes.
    private static XElement CreateNode(BG3ModuleRef module) =>
        new("node",
            new XAttribute("id", "ModuleShortDesc"),
            Attribute("Folder", "LSString", module.Folder),
            Attribute("MD5", "LSString", module.Md5),
            Attribute("Name", "LSString", module.Name),
            Attribute("PublishHandle", "uint64", module.PublishHandle.ToString(CultureInfo.InvariantCulture)),
            Attribute("UUID", "guid", module.Uuid),
            Attribute("Version64", "int64", module.Version64.ToString(CultureInfo.InvariantCulture)));

    private static XElement Attribute(string id, string type, string value) =>
        new("attribute",
            new XAttribute("id", id),
            new XAttribute("type", type),
            new XAttribute("value", value));

    private static bool HasId(XElement node, string id) =>
        string.Equals((string?)node.Attribute("id"), id, StringComparison.Ordinal);

    private static string ReadAttribute(XElement node, string id) =>
        (string?)node.Elements("attribute")
            .FirstOrDefault(a => HasId(a, id))?
            .Attribute("value") ?? string.Empty;

    private static string Display(BG3ModuleRef module) =>
        string.IsNullOrEmpty(module.Name) ? module.Uuid : module.Name;

    private static string Serialize(XDocument document, string? originalText)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "    ",
            OmitXmlDeclaration = true,

            // Whatever the file used before; the game writes it either way.
            NewLineChars = originalText?.Contains("\r\n", StringComparison.Ordinal) == true ? "\r\n" : "\n"
        };

        var body = new StringBuilder();
        using (var writer = XmlWriter.Create(body, settings))
            document.Root!.Save(writer);

        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + settings.NewLineChars +
               body + settings.NewLineChars;
    }

    // Puts the entries into the file in the given order and writes it, keeping a copy of
    // the file it replaces.
    private static void Write(
        string modSettingsPath,
        XDocument document,
        XElement modsChildren,
        IReadOnlyList<Entry> entries,
        string? originalText)
    {
        modsChildren.ReplaceNodes(entries.Select(e => e.Node));
        MirrorModOrder(document, entries.Select(e => e.Uuid).ToList());

        var text = Serialize(document, originalText);

        if (originalText != null)
        {
            // The first file ever replaced is kept for good; the latest one is kept too,
            // so a single bad write is always one copy away from undone.
            var original = modSettingsPath + OriginalSuffix;
            if (!File.Exists(original))
                File.Copy(modSettingsPath, original);

            File.Copy(modSettingsPath, modSettingsPath + BackupSuffix, overwrite: true);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(modSettingsPath)!);
        WriteAtomically(modSettingsPath, text);
    }

    // Written beside the target and moved over it, so the game never finds half a file.
    private static void WriteAtomically(string path, string text)
    {
        var temporary = path + ".modular.tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, path, overwrite: true);
    }

    private static HashSet<string> ReadOwnership(string path)
    {
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (File.Exists(path) &&
                JsonSerializer.Deserialize<Ownership>(File.ReadAllText(path)) is { Managed: { } uuids })
            {
                owned.UnionWith(uuids);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Without the record nothing is treated as Modular's, which only means an
            // old entry is left for the user to remove.
        }

        return owned;
    }

    private static void WriteOwnership(string path, List<string> owned, HashSet<string> previous)
    {
        if (owned.Count == 0 && previous.Count == 0)
            return;

        if (previous.SetEquals(owned) && File.Exists(path))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new Ownership { Managed = owned }, JsonOptions));
    }

    private sealed record Entry(string Uuid, string Name, XElement Node);

    private sealed class Ownership
    {
        public List<string> Managed { get; set; } = [];
    }
}
