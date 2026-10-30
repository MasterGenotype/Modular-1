using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Modular.Core.Installers.BaldursGate3;

/// <summary>
/// A Baldur's Gate 3 module as named in a <c>meta.lsx</c>: either the mod itself or one of
/// the modules it depends on.
/// </summary>
/// <param name="Uuid">The module's UUID, which is its identity. Lowercase.</param>
/// <param name="Name">Display name.</param>
/// <param name="Folder">The module's folder under <c>Mods/</c>.</param>
/// <param name="PublishHandle">The in-game mod browser's id for the module, or 0.</param>
/// <param name="Md5">The hash the meta records for the module, often empty.</param>
/// <param name="Version64">The module's version, packed as the game packs it.</param>
public sealed record BG3ModuleRef(
    string Uuid,
    string Name,
    string Folder,
    ulong PublishHandle,
    string Md5 = "",
    long Version64 = BG3ModuleRef.DefaultVersion64)
{
    /// <summary>Version 1.0.0.0, which is what a module with no version is given.</summary>
    public const long DefaultVersion64 = 36028797018963968;
}

/// <summary>
/// The parts of a pak's <c>Mods/&lt;Folder&gt;/meta.lsx</c> that identify the mod and say what
/// it needs.
/// </summary>
/// <param name="Module">The mod the pak contains.</param>
/// <param name="Dependencies">Modules the mod declares it depends on.</param>
/// <param name="Conflicts">Modules the mod declares it cannot be loaded alongside.</param>
public sealed record BG3PakMeta(
    BG3ModuleRef Module,
    IReadOnlyList<BG3ModuleRef> Dependencies,
    IReadOnlyList<BG3ModuleRef> Conflicts)
{
    /// <summary>The dependencies that are other mods, as opposed to parts of the game.</summary>
    public IEnumerable<BG3ModuleRef> ModDependencies =>
        Dependencies.Where(d => !IsBuiltIn(d.Uuid));

    /// <summary>
    /// The name other mods reach this mod's Script Extender scripts by
    /// (<c>Mods.&lt;name&gt;</c>), or <c>null</c> for a mod without scripts.
    /// </summary>
    public string? ScriptModTable { get; init; }

    /// <summary>
    /// Other mods' script tables this mod's scripts use while the game is loading, so
    /// those mods must load first. Read by <see cref="BG3ScriptReferences"/>.
    /// </summary>
    public IReadOnlyList<string> ScriptReferences { get; init; } = [];

    /// <summary>
    /// Modules that ship with the game. Nearly every mod lists several of these as
    /// dependencies, and none of them is ever something to go and fetch.
    /// </summary>
    private static readonly HashSet<string> BuiltInModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "991c9c7a-fb80-40cb-8f0d-b92d4e80e9b1", // Gustav
        "28ac9ce2-2aba-8cda-b3b5-6e922f71b6b8", // GustavDev
        "cb555efe-2d9e-131f-8195-a89329d218ea", // GustavX
        "ed539163-bb70-431b-96a7-f5b2eda5376b", // Shared
        "3d0c5ff8-c95d-c907-ff3e-34b204f1c630", // SharedDev
        "e5c9077e-1fca-4f24-b55d-464f512c98a8", // FW3
        "9dff4c3b-fda7-43de-a763-ce1383039999", // Engine
        "e842840a-2449-588c-b0c4-22122cfce31b", // DiceSet_01
        "b176a0ac-d79f-ed9d-5a87-5c2c80874e10", // DiceSet_02
        "e0a4d990-7b9b-8fa9-d7c6-04017c6cf5b1", // DiceSet_03
        "77a2155f-4b35-4f0c-e7ff-4338f91426a4", // DiceSet_04
        "6efc8f44-cc2a-0273-d4b1-681d3faa411b", // DiceSet_05
        "ee4989eb-aab8-968f-8674-812ea2f4bfd7", // DiceSet_06
        "bf19bab4-4908-ef39-9065-ced469c0f877", // DiceSet_07
        "b77b6210-ac50-4cb1-a3d5-5702fb9c744c", // Honour
        "767d0062-d82c-279c-e16b-dfee7fe94cdd", // HonourX
        "ee5a55ff-eb38-0b27-c5b0-f358dc306d34", // ModBrowser
        "630daa32-70f8-3da5-41b9-154fe8410236", // MainUI
        "e1ce736b-52e6-e713-e9e7-e6abbb15a198", // CrossplayUI
        "55ef175c-59e3-b44b-3fb2-8f86acc5d550", // PhotoMode
    };

    /// <summary>
    /// Whether a module ships with the game.
    /// </summary>
    /// <param name="uuid">Module UUID.</param>
    /// <returns>True for the base campaign, shared and UI modules.</returns>
    public static bool IsBuiltIn(string uuid) => BuiltInModules.Contains(uuid);

    /// <summary>
    /// Reads the metadata of the mod inside a pak.
    /// </summary>
    /// <param name="pakPath">Absolute path to the <c>.pak</c>.</param>
    /// <returns>
    /// The metadata, or <c>null</c> when the pak has no readable <c>meta.lsx</c> of its own.
    /// A pak without one is normal: override paks carry files and no module of their own.
    /// </returns>
    /// <remarks>
    /// A pak that overrides the game's own modules packs their <c>meta.lsx</c> beside its
    /// own (Party Limit Begone ships nine). Those describe the game, not the mod, so they
    /// are passed over.
    /// </remarks>
    public static BG3PakMeta? Read(string pakPath)
    {
        foreach (var metaPath in LspkReader.ReadFileNames(pakPath).Where(IsMetaPath))
        {
            var bytes = LspkReader.ReadFile(pakPath, name => name == metaPath);
            var meta = bytes == null ? null : Parse(bytes);

            if (meta != null && !IsBuiltIn(meta.Module.Uuid))
                return meta;
        }

        return null;
    }

    /// <summary>
    /// Parses the bytes of a <c>meta.lsx</c>.
    /// </summary>
    /// <param name="metaLsx">The file's contents, with or without a byte order mark.</param>
    /// <returns>The metadata, or <c>null</c> when it is not a usable <c>meta.lsx</c>.</returns>
    public static BG3PakMeta? Parse(byte[] metaLsx)
    {
        ArgumentNullException.ThrowIfNull(metaLsx);

        XDocument document;
        try
        {
            using var stream = new MemoryStream(metaLsx);
            document = XDocument.Load(stream);
        }
        catch (XmlException)
        {
            return null;
        }

        var nodes = document.Descendants("node").ToList();

        var module = nodes
            .Where(n => HasId(n, "ModuleInfo"))
            .Select(ReadModule)
            .FirstOrDefault(m => m != null);

        if (module == null)
            return null;

        return new BG3PakMeta(
            module,
            ReadReferences(nodes, "Dependencies"),
            ReadReferences(nodes, "Conflicts"));
    }

    private static List<BG3ModuleRef> ReadReferences(List<XElement> nodes, string listId) =>
        nodes
            .Where(n => HasId(n, listId))
            .SelectMany(n => n.Descendants("node"))
            .Where(n => HasId(n, "ModuleShortDesc"))
            .Select(ReadModule)
            .OfType<BG3ModuleRef>()
            .DistinctBy(d => d.Uuid)
            .ToList();

    /// <summary>
    /// Converts the 32-bit version older metas carry into the 64-bit packing.
    /// </summary>
    /// <param name="version">Major in the top 4 bits, then minor (4), revision (8), build (16).</param>
    /// <returns>Major in the top 9 bits, then minor (8), revision (16), build (31).</returns>
    public static long ToVersion64(int version)
    {
        long major = (version >> 28) & 0xF;
        long minor = (version >> 24) & 0xF;
        long revision = (version >> 16) & 0xFF;
        long build = version & 0xFFFF;

        return (major << 55) | (minor << 47) | (revision << 31) | build;
    }

    // Mods/<Folder>/meta.lsx, and nothing deeper.
    private static bool IsMetaPath(string path)
    {
        var parts = path.Split('/');

        return parts.Length == 3 &&
               string.Equals(parts[0], "Mods", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(parts[2], "meta.lsx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasId(XElement node, string id) =>
        string.Equals((string?)node.Attribute("id"), id, StringComparison.Ordinal);

    /// <summary>What the game makes of a module UUID that is not a GUID.</summary>
    public const string InvalidUuid = "00000000-0000-0000-0000-000000000000";

    // Attribute types vary between metas (guid or FixedString for the UUID, for one), so
    // only ids and values are read.
    private static BG3ModuleRef? ReadModule(XElement node)
    {
        string Value(string id) =>
            (string?)node.Elements("attribute")
                .FirstOrDefault(a => HasId(a, id))?
                .Attribute("value") ?? string.Empty;

        var uuid = Value("UUID").Trim().ToLowerInvariant();
        if (uuid.Length == 0)
            return null;

        // Some old mods declare a UUID that is not a GUID ("goldweig-htze-ro12-..."). The
        // game reads those as all zeros and lists them that way in modsettings.lsx, so they
        // are read the same way here; otherwise the game's own entry goes unrecognised and
        // a second, malformed one is added.
        if (!Guid.TryParse(uuid, out _))
            uuid = InvalidUuid;

        _ = ulong.TryParse(
            Value("PublishHandle"), NumberStyles.None, CultureInfo.InvariantCulture, out var handle);

        // Current metas carry Version64; older ones a 32-bit Version in a different packing.
        long version64;
        if (!long.TryParse(Value("Version64"), NumberStyles.Integer, CultureInfo.InvariantCulture, out version64))
        {
            version64 = int.TryParse(
                Value("Version"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var version32)
                ? ToVersion64(version32)
                : BG3ModuleRef.DefaultVersion64;
        }

        return new BG3ModuleRef(
            uuid, Value("Name"), Value("Folder"), handle, Value("MD5"), version64);
    }
}
