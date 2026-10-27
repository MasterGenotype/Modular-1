using System.Text.Json;
using System.Text.RegularExpressions;
using Modular.Core.Archives;
using Modular.Core.Dependencies.Conventions;
using Modular.Core.Installers;
using Modular.Core.Utilities;
using Modular.Sdk.Archives;
using Modular.Sdk.Compatibility;

namespace Modular.Core.Compatibility;

/// <summary>
/// Lists the mods Modular installed, from their install records.
/// </summary>
/// <remarks>
/// Each record is traced to its hub the way <see cref="Dependencies.MissingDependencyService"/>
/// does: the download record or packaging beside the archive first, then a NexusMods file
/// name. The installed version comes from the NexusMods file name.
/// </remarks>
public sealed class ChangesetInstalledModSource : IInstalledModSource
{
    private readonly IReadOnlyList<ChangesetRecord> _records;
    private readonly string? _gameDomain;
    private readonly IArchiveReaderFactory _archiveReaderFactory;

    /// <summary>Creates the source.</summary>
    /// <param name="records">The game's committed install records.</param>
    /// <param name="gameDomain">The game's hub domain, for records whose packaging does not say.</param>
    /// <param name="archiveReaderFactory">Opens archives to read the metadata packed in them.</param>
    public ChangesetInstalledModSource(
        IEnumerable<ChangesetRecord> records,
        string? gameDomain,
        IArchiveReaderFactory? archiveReaderFactory = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records.ToList();
        _gameDomain = gameDomain;
        _archiveReaderFactory = archiveReaderFactory ?? new ArchiveReaderFactory();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<InstalledMod>> ListAsync(CancellationToken ct = default)
    {
        var mods = new List<InstalledMod>();
        foreach (var record in _records)
        {
            ct.ThrowIfCancellationRequested();

            var source = DependencyConventions.Read(record.ArchivePath, _archiveReaderFactory).Source;
            var name = NexusFileName.ParseModName(record.ArchivePath)
                       ?? record.ModId
                       ?? Path.GetFileNameWithoutExtension(record.ArchivePath)
                       ?? record.ChangesetId;
            var version = ParseNexusVersion(record.ArchivePath);
            var domain = source?.GameDomain ?? _gameDomain;

            mods.Add(new InstalledMod
            {
                Key = source != null
                    ? InstalledMod.HubKey(source.BackendId, domain, source.ModId)
                    : "changeset:" + record.ChangesetId,
                Name = name,
                BackendId = source?.BackendId,
                GameDomain = domain,
                ModId = source?.ModId,
                Versions = version == null ? [] : [version],
                Files = ReadFiles(record)
            });
        }

        // The same mod installed from several archives is one mod with several versions.
        IReadOnlyList<InstalledMod> merged = mods
            .GroupBy(m => m.Key, StringComparer.Ordinal)
            .Select(g => g.First() with
            {
                Versions = g.SelectMany(m => m.Versions).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Files = g.SelectMany(m => m.Files).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            })
            .ToList();

        return Task.FromResult(merged);
    }

    // "<name>-<modId>-<version>-<unix time>.<ext>", where the version's dots became dashes.
    private static readonly Regex NexusName = new(
        @"^.+?-(?<id>\d+)-(?<version>.+)-(?<time>\d{9,11})$", RegexOptions.Compiled);

    /// <summary>Reads the version a NexusMods download name carries, or <c>null</c>.</summary>
    public static string? ParseNexusVersion(string? archivePath)
    {
        var name = Path.GetFileNameWithoutExtension(archivePath);
        var match = name == null ? null : NexusName.Match(name);
        return match is { Success: true } ? match.Groups["version"].Value.Replace('-', '.') : null;
    }

    // Game-relative paths of the record's files: what it placed in the game folder, or for
    // a deployment-managed record whose files went elsewhere, what it staged.
    private static IReadOnlyList<string> ReadFiles(ChangesetRecord record)
    {
        if (string.IsNullOrEmpty(record.OperationsJson))
            return [];

        JsonElement ops;
        try
        {
            ops = JsonSerializer.Deserialize<JsonElement>(record.OperationsJson);
        }
        catch (JsonException)
        {
            return [];
        }

        var root = record.TargetDirectory;
        var files = new List<string>();

        foreach (var property in new[] { "deployedFiles", "installedFiles" })
        {
            foreach (var path in Strings(ops, property))
            {
                if (!Path.IsPathRooted(path))
                    files.Add(path.Replace('\\', '/'));
                else if (root != null && path.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal))
                    files.Add(Path.GetRelativePath(root, path).Replace('\\', '/'));
            }
        }

        if (files.Count == 0)
            files.AddRange(Strings(ops, "stagedFiles").Select(p => p.Replace('\\', '/')));

        return files;
    }

    private static IEnumerable<string> Strings(JsonElement ops, string property) =>
        ops.ValueKind == JsonValueKind.Object &&
        ops.TryGetProperty(property, out var array) &&
        array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)
            : [];
}

/// <summary>
/// Lists mods from an inventory file: for a game modded by hand or by another manager,
/// where Modular has no install records.
/// </summary>
/// <remarks>
/// The file names each mod, where it came from, and its files relative to the game folder:
/// <code>
/// {
///   "backend": "nexusmods", "gameDomain": "cyberpunk2077",
///   "mods": [ { "name": "Codeware", "modId": "7780", "versions": ["1.21.1"], "files": ["red4ext/plugins/Codeware/Codeware.dll"] } ]
/// }
/// </code>
/// A mod may set its own <c>backend</c> and <c>gameDomain</c>; without a <c>modId</c> it is
/// known by name only.
/// </remarks>
public sealed class InventoryInstalledModSource : IInstalledModSource
{
    private readonly string _path;

    /// <summary>Creates the source.</summary>
    /// <param name="path">The inventory file.</param>
    public InventoryInstalledModSource(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InstalledMod>> ListAsync(CancellationToken ct = default)
    {
        Inventory? inventory;
        await using (var stream = File.OpenRead(_path))
        {
            inventory = await JsonSerializer.DeserializeAsync<Inventory>(
                stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
        }

        if (inventory?.Mods == null)
            return [];

        return inventory.Mods
            .Where(m => !string.IsNullOrWhiteSpace(m.Name) || !string.IsNullOrWhiteSpace(m.ModId))
            .Select(m =>
            {
                var backend = m.Backend ?? inventory.Backend;
                var domain = m.GameDomain ?? inventory.GameDomain;
                var traced = !string.IsNullOrEmpty(backend) && !string.IsNullOrEmpty(m.ModId);
                var versions = (m.Versions ?? []).Concat(m.Version == null ? [] : [m.Version]);

                return new InstalledMod
                {
                    Key = traced ? InstalledMod.HubKey(backend!, domain, m.ModId!) : "name:" + m.Name!.ToLowerInvariant(),
                    Name = m.Name ?? m.ModId!,
                    BackendId = traced ? backend : null,
                    GameDomain = domain,
                    ModId = traced ? m.ModId : null,
                    Versions = versions.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToList(),
                    Files = (m.Files ?? []).Select(f => f.Replace('\\', '/')).ToList()
                };
            })
            .GroupBy(m => m.Key, StringComparer.Ordinal)
            .Select(g => g.First() with
            {
                Versions = g.SelectMany(m => m.Versions).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Files = g.SelectMany(m => m.Files).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            })
            .ToList();
    }

    private sealed record Inventory(string? Backend, string? GameDomain, List<InventoryMod>? Mods);

    private sealed record InventoryMod(
        string? Name,
        string? ModId,
        string? Backend,
        string? GameDomain,
        string? Version,
        List<string>? Versions,
        List<string>? Files);
}
