using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modular.Core.ModLists;

/// <summary>
/// A game's installed mods as a portable file (<c>*.modular-modlist.json</c>): what another
/// Modular needs to download and install the same files in the same order. Mods with no known
/// download source are listed for the user to supply.
/// </summary>
public sealed record ModList
{
    public const string FileKind = "modular-modlist";
    public const string Extension = ".modular-modlist.json";

    public int Schema { get; init; } = 1;
    public string Kind { get; init; } = FileKind;
    public required ModListGame Game { get; init; }
    public DateTimeOffset ExportedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? ExportedBy { get; init; }

    /// <summary>Install order, first to last (the last wins file conflicts).</summary>
    public List<ModListEntry> Mods { get; init; } = new();

    /// <summary>Things the list can't carry, said once to whoever imports it.</summary>
    public List<string> Notes { get; init; } = new();

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static ModList Load(string path)
    {
        var list = JsonSerializer.Deserialize<ModList>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException($"{path} is empty.");
        if (list.Kind != FileKind)
            throw new InvalidDataException($"{path} is not a Modular mod list (kind '{list.Kind}').");
        if (list.Schema > 1)
            throw new InvalidDataException($"{path} uses mod list schema {list.Schema}; this Modular reads schema 1. Update Modular.");
        return list;
    }
}

/// <summary>The game a list is for.</summary>
public sealed record ModListGame(string Key, string? Name, int? SteamAppId, string? NexusDomain, string? BuildId)
{
    /// <summary>The game build the list was made on (true depot manifests), for a mismatch warning on import.</summary>
    public Modular.Core.Depots.BuildStamp? Build { get; init; }
}

/// <summary>One mod in a list.</summary>
public sealed record ModListEntry
{
    public int Order { get; init; }
    /// <summary>The id the mod is installed under (e.g. "3620", "24893-2", or a name).</summary>
    public required string ModId { get; init; }
    public required string Name { get; init; }
    public bool Enabled { get; init; } = true;
    public string? Installer { get; init; }
    /// <summary>Where to download the exact file; null when only the user has it.</summary>
    public ModListSource? Source { get; init; }
    /// <summary>The archive's file name, size and SHA-256, to recognise the same file.</summary>
    public string? ArchiveName { get; init; }
    public long? Size { get; init; }
    public string? Sha256 { get; init; }
    /// <summary>Why the entry needs the user, e.g. the installed file was removed from the mod page.</summary>
    public string? Note { get; init; }
}

/// <summary>A downloadable file on a mod site.</summary>
public sealed record ModListSource(string Backend, string Game, string ModId, string? FileId)
{
    /// <summary>The mod's page, for files that can't be downloaded automatically.</summary>
    public string? PageUrl => Backend == "nexusmods" ? $"https://www.nexusmods.com/{Game}/mods/{ModId}?tab=files" : null;
}
