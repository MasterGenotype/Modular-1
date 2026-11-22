using System.Text.Json;
using Modular.Core.Database;
using Modular.Core.Deployment;
using Modular.Core.Installers;
using Modular.Core.Modules;

namespace Modular.Core.ModLists;

/// <summary>One file on a mod's download page, for matching an archive to its file id.</summary>
public sealed record RemoteModFile(string FileId, string FileName, long? Size, string? Version = null, DateTimeOffset? UploadedAt = null,
    string? Category = null);

/// <summary>
/// Builds a <see cref="ModList"/> from what Modular installed for a game. The download source
/// comes from the archive's record (<c>.modular.json</c>), the install's source, or a numeric
/// (Nexus) mod id; the exact file id from the record, else by matching the archive's name or
/// size against the mod's file list online.
/// </summary>
public sealed partial class ModListExporter
{
    private readonly ModularDatabase _db;
    private readonly GameModuleRegistry _modules;

    public ModListExporter(ModularDatabase db, GameModuleRegistry? modules = null)
    {
        _db = db;
        _modules = modules ?? new GameModuleRegistry();
    }

    /// <param name="lookups">Online lookups (Nexus, mod.io); null stays offline.</param>
    public async Task<ModList> ExportAsync(
        string gameKey, string? gameName, string? buildId, ModListLookups? lookups,
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

            // Source: the archive's record, then the install's own, then what the archive's place
            // and name say (a mod.io download, or a Nexus-style numeric id).
            string? backend = record?.Backend ?? (mod.Source is { Length: > 0 } s && s != "local" ? s : null);
            string? remoteId = record?.ModId;
            string? fileId = record?.FileId ?? mod.SourceFileId;
            string? game = record?.GameDomain;
            string? note = null;
            if (backend is null && LooksLikeModIo(mod.ModId, archive, archiveName))
                backend = "modio";

            if (backend == "modio")
            {
                if (remoteId is null && archiveName is not null && ModIoNumbered().Match(archiveName) is { Success: true } numbered)
                    remoteId = numbered.Groups[1].Value;
                if (lookups is { HasModIo: true })
                {
                    try
                    {
                        // The install id "modio-<name_id>" or the archive's folder (its mod's title) finds the mod.
                        var nameId = mod.ModId.StartsWith("modio-", StringComparison.OrdinalIgnoreCase) && remoteId is null
                            ? mod.ModId["modio-".Length..] : null;
                        var title = archive is not null ? Path.GetFileName(Path.GetDirectoryName(archive)) : null;
                        var found = remoteId is not null ? null : await lookups.FindModIoAsync(nameId, title, ct);
                        if (found is not null)
                        {
                            remoteId = found.ModId;
                            game = found.Game;
                            if (name == mod.ModId)
                                name = found.Name;
                        }
                        if (remoteId is not null && fileId is null)
                        {
                            var files = await lookups.ListFilesAsync("modio", game, remoteId, ct);
                            fileId = MatchFile(files, archiveName, size)?.FileId;
                            if (fileId is null && files.Count > 0)
                                note = "The installed file is no longer on the mod page.";
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Leave it for the user to find.
                    }
                }
                game ??= "";
            }

            var idPart = mod.ModId.Split('-')[0];
            if (backend is null && ModDisplayNames.IsNumericId(idPart) && nexusDomain is not null)
                backend = "nexusmods";
            if (backend == "nexusmods")
            {
                remoteId ??= ModDisplayNames.IsNumericId(idPart) ? idPart : null;
                game ??= nexusDomain;
            }

            if (backend == "nexusmods" && remoteId is not null && fileId is null && lookups is { HasNexus: true })
            {
                foreach (var domain in record?.GameDomain is { } d ? [d] : domains)
                {
                    IReadOnlyList<RemoteModFile> files;
                    try
                    {
                        files = await lookups.ListFilesAsync("nexusmods", domain, remoteId, ct);
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
                Choices = mod.InstallChoices is { } answers ? new Dictionary<string, string>(answers) : null,
                Note = note ?? (source is null ? "Installed from a local file; no download source is known." : null),
            });
        }

        var notes = new List<string>();
        var withoutAnswers = entries.Count(e => e.Choices is null);
        if (withoutAnswers > 0)
            notes.Add($"{withoutAnswers} mod(s) carry no saved installer answers (installed before Modular saved them, or without options); "
                + "if one has options, the importing Modular asks for them.");
        if (noFileId > 0)
            notes.Add($"{noFileId} mod(s) have a mod page but no exact file; the importer lists them to pick a file by hand.");
        if (manual > 0)
            notes.Add($"{manual} mod(s) have no download source (installed from local files); the importer lists them to supply by hand.");

        int? steamAppId = int.TryParse(gameKey, out var appId) ? appId : null;
        var build = steamAppId is { } a ? Modular.Core.Depots.BuildStamp.ForApp((uint)a) : null;
        return new ModList
        {
            Game = new ModListGame(gameKey, gameName, steamAppId, nexusDomain, build?.BuildId?.ToString() ?? buildId) { Build = build },
            ExportedBy = $"Modular {typeof(ModListExporter).Assembly.GetName().Version}",
            Mods = entries,
            Notes = notes,
        };
    }

    /// <summary>
    /// The upload time (and version) a Nexus download name carries: Unix seconds at the end of
    /// "Name-id-ver-1717876940", or "Name id 2.0 2026-11-15T19-05Z abc" in the newer form.
    /// </summary>
    public static (DateTimeOffset? Uploaded, string? Version) UploadInfoFromName(string? archiveName)
    {
        var stem = archiveName is null ? "" : Path.GetFileNameWithoutExtension(archiveName);
        if (System.Text.RegularExpressions.Regex.Match(stem, @"-(\d{10})$") is { Success: true } unix)
            return (DateTimeOffset.FromUnixTimeSeconds(long.Parse(unix.Groups[1].Value)), null);
        if (System.Text.RegularExpressions.Regex.Match(stem, @" (\S+) (\d{4}-\d{2}-\d{2})T(\d{2})-(\d{2})Z \w+$") is { Success: true } iso)
            return (DateTimeOffset.Parse($"{iso.Groups[2].Value}T{iso.Groups[3].Value}:{iso.Groups[4].Value}:00Z"), iso.Groups[1].Value);
        return (null, null);
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

        var (uploaded, version) = UploadInfoFromName(archiveName);
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

    /// <summary>A mod.io download: under the mods folder's modio/ tree, named "modio-&lt;id&gt;-…", or installed as "modio-…".</summary>
    private static bool LooksLikeModIo(string modId, string? archive, string? archiveName) =>
        modId.StartsWith("modio-", StringComparison.OrdinalIgnoreCase)
        || (archiveName is not null && ModIoNumbered().IsMatch(archiveName))
        || (archive is not null && archive.Replace('\\', '/').Contains("/modio/", StringComparison.OrdinalIgnoreCase));

    [System.Text.RegularExpressions.GeneratedRegex(@"^modio-(\d+)-")]
    private static partial System.Text.RegularExpressions.Regex ModIoNumbered();

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
