using System.Text.Json;
using Modular.Core.Database;
using Modular.Core.Deployment;
using Modular.Core.Installers;
using Modular.Core.Modules;

namespace Modular.Core.ModLists;

/// <summary>One file on a mod's download page, for matching an archive to its file id.</summary>
public sealed record RemoteModFile(string FileId, string FileName, long? Size, string? Version = null, DateTimeOffset? UploadedAt = null);

/// <summary>
/// Builds a <see cref="ModList"/> from what Modular installed for a game. The download source
/// comes from the archive's record (<c>.modular.json</c>), the install's source, or a numeric
/// (Nexus) mod id; the exact file id from the record, else by matching the archive's name or
/// size against the mod's file list online.
/// </summary>
public sealed class ModListExporter
{
    private readonly ModularDatabase _db;
    private readonly GameModuleRegistry _modules;

    public ModListExporter(ModularDatabase db, GameModuleRegistry? modules = null)
    {
        _db = db;
        _modules = modules ?? new GameModuleRegistry();
    }

    /// <param name="listFiles">Lists a mod's files on Nexus (domain, mod id); null stays offline.</param>
    public async Task<ModList> ExportAsync(
        string gameKey, string? gameName, string? buildId,
        Func<string, string, CancellationToken, Task<IReadOnlyList<RemoteModFile>>>? listFiles,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var repo = new GameModRepository(_db);
        var changesets = new ChangesetManager(_db);
        var domains = new ModNameRepair(_db, null, _modules).DomainsFor(gameKey);
        var nexusDomain = domains.FirstOrDefault();
        var mods = await repo.ListAsync(gameKey, ct: ct);

        var entries = new List<ModListEntry>();
        var noFileId = 0;
        var manual = 0;
        foreach (var mod in mods)
        {
            ct.ThrowIfCancellationRequested();
            var name = string.IsNullOrWhiteSpace(mod.DisplayName) ? mod.ModId : mod.DisplayName!;
            progress?.Report(name);
            var archive = mod.ChangesetId is { } cs ? (await changesets.GetChangesetAsync(cs, ct))?.ArchivePath : null;
            var record = archive is null ? null : ReadRecord(archive + ".modular.json");

            var archiveName = record?.FileName ?? (archive is null ? null : Path.GetFileName(archive));
            long? size = record?.Size ?? (archive is not null && File.Exists(archive) ? new FileInfo(archive).Length : null);

            // Source: the archive's record, then the install's own, then a Nexus-style numeric id.
            string? backend = record?.Backend ?? (mod.Source is { Length: > 0 } s && s != "local" ? s : null);
            string? remoteId = record?.ModId;
            string? fileId = record?.FileId ?? mod.SourceFileId;
            var idPart = mod.ModId.Split('-')[0];
            if (backend is null && ModDisplayNames.IsNumericId(idPart) && nexusDomain is not null)
                backend = "nexusmods";
            remoteId ??= ModDisplayNames.IsNumericId(idPart) ? idPart : null;
            var game = record?.GameDomain ?? nexusDomain;

            string? note = null;
            if (backend == "nexusmods" && remoteId is not null && fileId is null && listFiles is not null)
            {
                foreach (var domain in record?.GameDomain is { } d ? [d] : domains)
                {
                    IReadOnlyList<RemoteModFile> files;
                    try
                    {
                        files = await listFiles(domain, remoteId, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        continue;
                    }
                    var match = MatchFile(files, archiveName, size);
                    if (files.Count > 0)
                    {
                        game = domain;
                        if (match is null)
                        {
                            var current = files.Where(f => f.UploadedAt is not null).OrderByDescending(f => f.UploadedAt).FirstOrDefault();
                            note = "The installed file is no longer on the mod page" +
                                (current is null ? "." : $"; the newest there is {current.FileName}{(current.Version is { } v ? $" (v{v})" : "")}.");
                        }
                    }
                    if (match is not null)
                    {
                        fileId = match.FileId;
                        break;
                    }
                }
            }

            ModListSource? source = backend is not null && remoteId is not null && game is not null
                ? new ModListSource(backend, game, remoteId, fileId)
                : null;
            if (source is null)
                manual++;
            else if (fileId is null)
                noFileId++;

            entries.Add(new ModListEntry
            {
                Order = entries.Count + 1,
                ModId = mod.ModId,
                Name = name,
                Enabled = mod.Enabled,
                Installer = mod.InstallerId,
                Source = source,
                ArchiveName = archiveName,
                Size = size,
                Sha256 = record?.Sha256,
                Note = note ?? (source is null ? "Installed from a local file; no download source is known." : null),
            });
        }

        var notes = new List<string>
        {
            "Installer choices (FOMOD options) are not recorded; the importing Modular uses each installer's defaults.",
        };
        if (noFileId > 0)
            notes.Add($"{noFileId} mod(s) have a mod page but no exact file; the importer lists them to pick a file by hand.");
        if (manual > 0)
            notes.Add($"{manual} mod(s) have no download source (installed from local files); the importer lists them to supply by hand.");

        int? steamAppId = int.TryParse(gameKey, out var appId) ? appId : null;
        return new ModList
        {
            Game = new ModListGame(gameKey, gameName, steamAppId, nexusDomain, buildId),
            ExportedBy = $"Modular {typeof(ModListExporter).Assembly.GetName().Version}",
            Mods = entries,
            Notes = notes,
        };
    }

    /// <summary>
    /// The Nexus file an archive came from: same file name; else the upload time Nexus puts in
    /// download names (Unix seconds in "Name-id-ver-1717876940", or "2026-11-15T19-05Z" with the
    /// version in the newer "Name id 2.0 2026-11-15T19-05Z abc" form); else a unique size match
    /// (Nexus rounds sizes to KB).
    /// </summary>
    public static RemoteModFile? MatchFile(IReadOnlyList<RemoteModFile> files, string? archiveName, long? size)
    {
        if (files.Count == 0)
            return null;
        if (archiveName is not null && files.FirstOrDefault(f => f.FileName.Equals(archiveName, StringComparison.OrdinalIgnoreCase)) is { } byName)
            return byName;

        var stem = archiveName is null ? "" : Path.GetFileNameWithoutExtension(archiveName);
        DateTimeOffset? uploaded = null;
        string? version = null;
        if (System.Text.RegularExpressions.Regex.Match(stem, @"-(\d{10})$") is { Success: true } unix)
            uploaded = DateTimeOffset.FromUnixTimeSeconds(long.Parse(unix.Groups[1].Value));
        else if (System.Text.RegularExpressions.Regex.Match(stem, @" (\S+) (\d{4}-\d{2}-\d{2})T(\d{2})-(\d{2})Z \w+$") is { Success: true } iso)
        {
            version = iso.Groups[1].Value;
            uploaded = DateTimeOffset.Parse($"{iso.Groups[2].Value}T{iso.Groups[3].Value}:{iso.Groups[4].Value}:00Z");
        }
        if (uploaded is { } at)
        {
            var near = files.Where(f => f.UploadedAt is { } u && Math.Abs((u - at).TotalMinutes) < 2).ToList();
            if (near.Count == 1)
                return near[0];
            if (version is not null && near.Where(f => f.Version == version).ToList() is [var exact])
                return exact;
        }
        if (size is { } sz && files.Where(f => f.Size is { } fs && Math.Abs(fs - sz) < 1024).ToList() is [var bySize])
            return bySize;
        return null;
    }

    private sealed record ArchiveRecord(string? Backend, string? ModId, string? GameDomain, string? FileId, string? FileName, long? Size, string? Sha256);

    private static ArchiveRecord? ReadRecord(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            string? Str(JsonElement e, string name) =>
                e.TryGetProperty(name, out var v) ? v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Number => v.GetRawText(),
                    _ => null,
                } : null;
            var released = root.TryGetProperty("Released", out var r) ? r : default;
            long? size = released.ValueKind == JsonValueKind.Object && released.TryGetProperty("Size", out var sz) && sz.TryGetInt64(out var n) ? n : null;
            return new ArchiveRecord(Str(root, "Backend"), Str(root, "ModId"), Str(root, "GameDomain"), Str(root, "FileId"),
                released.ValueKind == JsonValueKind.Object ? Str(released, "FileName") : null, size,
                released.ValueKind == JsonValueKind.Object ? Str(released, "Sha256") : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
