using SteamKit2;

namespace Modular.Core.Depots;

/// <summary>
/// Modular's own copy of every depot manifest it has seen. Steam deletes stale manifests from
/// <c>depotcache</c> on update, so anything Modular may roll back to has to be kept here.
/// Layout matches depotcache: <c>&lt;root&gt;/&lt;depot&gt;_&lt;manifest&gt;.manifest</c>.
/// </summary>
public sealed class DepotManifestStore
{
    public DepotManifestStore(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "Modular", "depots");
    }

    public string Root { get; }

    public string PathFor(uint depotId, ulong manifestId) =>
        Path.Combine(Root, $"{depotId}_{manifestId}.manifest");

    public bool Contains(uint depotId, ulong manifestId) => File.Exists(PathFor(depotId, manifestId));

    public DepotManifest? Load(uint depotId, ulong manifestId)
    {
        var path = PathFor(depotId, manifestId);
        return File.Exists(path) ? DepotManifest.LoadFromFile(path) : null;
    }

    /// <summary>Saves a manifest with decrypted filenames. Writes to a temp file first so a
    /// half-written manifest never shadows a good one.</summary>
    public string Save(DepotManifest manifest)
    {
        if (manifest.FilenamesEncrypted)
            throw new InvalidOperationException("Decrypt filenames before archiving a manifest.");
        Directory.CreateDirectory(Root);
        var path = PathFor(manifest.DepotID, manifest.ManifestGID);
        var tmp = path + ".tmp";
        manifest.SaveToFile(tmp);
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    /// <summary>All archived manifests for one depot, newest creation time first.</summary>
    public IReadOnlyList<ArchivedManifest> List(uint? depotId = null)
    {
        if (!Directory.Exists(Root))
            return Array.Empty<ArchivedManifest>();
        var pattern = depotId is null ? "*.manifest" : $"{depotId}_*.manifest";
        return Directory.EnumerateFiles(Root, pattern)
            .Select(ArchivedManifest.TryParse)
            .OfType<ArchivedManifest>()
            .OrderByDescending(m => File.GetLastWriteTimeUtc(m.Path))
            .ToList();
    }

    /// <summary>
    /// Copies manifests from Steam's depotcache that the store does not have yet. The copy is
    /// checked by loading it; files SteamKit2 cannot read are reported, not copied.
    /// </summary>
    public DepotcacheImportResult ImportFromDepotcache(string depotcacheDir, Func<uint, bool>? depotFilter = null)
    {
        var imported = new List<ArchivedManifest>();
        var skipped = 0;
        var unreadable = new List<string>();
        if (!Directory.Exists(depotcacheDir))
            return new(imported, skipped, unreadable);

        Directory.CreateDirectory(Root);
        foreach (var file in Directory.EnumerateFiles(depotcacheDir, "*.manifest"))
        {
            var parsed = ArchivedManifest.TryParse(file);
            if (parsed is null || (depotFilter is not null && !depotFilter(parsed.DepotId)))
                continue;
            if (Contains(parsed.DepotId, parsed.ManifestId))
            {
                skipped++;
                continue;
            }
            try
            {
                var manifest = DepotManifest.LoadFromFile(file);
                if (manifest is null || manifest.FilenamesEncrypted)
                {
                    unreadable.Add(Path.GetFileName(file));
                    continue;
                }
                var dest = PathFor(parsed.DepotId, parsed.ManifestId);
                File.Copy(file, dest + ".tmp", overwrite: true);
                File.Move(dest + ".tmp", dest, overwrite: true);
                imported.Add(parsed with { Path = dest });
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
            {
                unreadable.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return new(imported, skipped, unreadable);
    }
}

public sealed record ArchivedManifest(uint DepotId, ulong ManifestId, string Path)
{
    public static ArchivedManifest? TryParse(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var parts = name.Split('_');
        return parts.Length == 2
               && uint.TryParse(parts[0], out var depot)
               && ulong.TryParse(parts[1], out var manifest)
            ? new ArchivedManifest(depot, manifest, path)
            : null;
    }
}

public sealed record DepotcacheImportResult(
    IReadOnlyList<ArchivedManifest> Imported, int AlreadyArchived, IReadOnlyList<string> Unreadable);
