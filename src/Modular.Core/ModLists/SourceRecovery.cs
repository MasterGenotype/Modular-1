using System.Text.Json;
using Modular.Core.Database;
using Modular.Core.Dependencies.Conventions;
using Modular.Core.Deployment;
using Modular.Core.Installers;
using Modular.Core.Modules;

namespace Modular.Core.ModLists;

/// <summary>How a mod's source was found, or why not.</summary>
public sealed record RecoveredSource(string ModId, string Name, DeclaredSource? Source, string Method, string? ArchivePath);

/// <summary>
/// Fills in where installed mods came from when their archive records lack it (downloads made
/// before every download path wrote one): Modular's download history first (exact), then
/// mod.io by the mod-title folder or numbered file name, then Nexus by mod number; the exact
/// file is matched by name where the site lists it. Writes only the record's source fields.
/// </summary>
public sealed class SourceRecovery
{
    private readonly ModularDatabase _db;
    private readonly string _downloadHistoryPath;
    private readonly GameModuleRegistry _modules;

    public SourceRecovery(ModularDatabase db, string downloadHistoryPath, GameModuleRegistry? modules = null)
    {
        _db = db;
        _downloadHistoryPath = downloadHistoryPath;
        _modules = modules ?? new GameModuleRegistry();
    }

    private sealed record HistoryEntry(string Backend, string Game, string ModId, string FileId, string FileName, string Path);

    private List<HistoryEntry> LoadHistory()
    {
        var list = new List<HistoryEntry>();
        if (!File.Exists(_downloadHistoryPath))
            return list;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_downloadHistoryPath));
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                string Str(string n) => e.TryGetProperty(n, out var v) ? v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.GetString() ?? "" : "";
                var url = Str("url");
                var backend = url.Contains("nexusmods.com", StringComparison.OrdinalIgnoreCase) ? "nexusmods"
                    : url.Contains("gamebanana.com", StringComparison.OrdinalIgnoreCase) ? "gamebanana"
                    : url.Contains("mod.io", StringComparison.OrdinalIgnoreCase) || url.Contains("modapi.io", StringComparison.OrdinalIgnoreCase) ? "modio"
                    : null;
                var modId = Str("mod_id");
                if (backend is null || modId.Length == 0 || modId == "0")
                    continue;
                list.Add(new HistoryEntry(backend, Str("game_domain"), modId, Str("file_id"), Str("filename"), Str("filepath")));
            }
        }
        catch (JsonException)
        {
        }
        return list;
    }

    public async Task<IReadOnlyList<RecoveredSource>> RecoverAsync(
        string gameKey, ModListLookups? lookups, bool apply, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var history = LoadHistory();
        var byPath = history.GroupBy(h => h.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var byName = history.GroupBy(h => h.FileName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var domains = new ModNameRepair(_db, null, _modules).DomainsFor(gameKey);
        var changesets = new ChangesetManager(_db);
        var results = new List<RecoveredSource>();

        foreach (var mod in await new GameModRepository(_db).ListAsync(gameKey, ct: ct))
        {
            ct.ThrowIfCancellationRequested();
            var name = string.IsNullOrWhiteSpace(mod.DisplayName) ? mod.ModId : mod.DisplayName!;
            var archive = mod.ChangesetId is { } cs ? (await changesets.GetChangesetAsync(cs, ct))?.ArchivePath : null;
            if (archive is null)
            {
                results.Add(new RecoveredSource(mod.ModId, name, null, "no archive recorded", null));
                continue;
            }
            var existing = DependencyConventions.ReadDownloadRecord(archive).Source;
            if (existing is { FileId: not null })
                continue; // already known exactly
            var fileName = ReleasedFileName(archive) ?? Path.GetFileName(archive);
            progress?.Report($"Tracing {name}…");

            DeclaredSource? found = null;
            var method = "";
            // 1. Modular's download history: exact mod and file ids.
            if ((byPath.TryGetValue(archive, out var h) || byName.TryGetValue(fileName, out h)) && h is not null)
            {
                found = new DeclaredSource(h.Backend, h.ModId, h.Game.Length > 0 ? h.Game : null, h.FileId.Length > 0 ? h.FileId : null);
                method = "download history";
            }
            // 2. mod.io: numbered file name, install id, or the mod-title folder.
            else if (lookups is { HasModIo: true } && (existing?.BackendId == "modio" || LooksLikeModIo(mod.ModId, archive, fileName)))
            {
                try
                {
                    string? modioId = existing?.BackendId == "modio" ? existing.ModId : NumberedModIo(fileName);
                    string? game = existing?.GameDomain;
                    if (modioId is null)
                    {
                        var nameId = mod.ModId.StartsWith("modio-", StringComparison.OrdinalIgnoreCase) ? mod.ModId["modio-".Length..] : null;
                        var title = Path.GetFileName(Path.GetDirectoryName(archive));
                        if (await lookups.FindModIoAsync(nameId, title, ct) is { } m)
                            (modioId, game) = (m.ModId, m.Game);
                    }
                    if (modioId is not null)
                    {
                        var files = await lookups.ListFilesAsync("modio", game, modioId, ct);
                        var file = ModListExporter.MatchFile(files, fileName, null);
                        found = new DeclaredSource("modio", modioId, string.IsNullOrEmpty(game) ? null : game, file?.FileId);
                        method = file is null ? "mod.io (mod found; that file is no longer listed)" : "mod.io";
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    method = $"mod.io lookup failed: {ex.Message}";
                }
            }
            // 3. Nexus by mod number (install id or record), file matched by name.
            else if (lookups is { HasNexus: true } &&
                     ((existing?.BackendId == "nexusmods" ? existing.ModId : null) ?? NexusNumber(mod.ModId)) is { } nexusId)
            {
                foreach (var domain in existing?.GameDomain is { } d ? [d] : domains)
                {
                    try
                    {
                        var files = await lookups.ListFilesAsync("nexusmods", domain, nexusId, ct);
                        if (files.Count == 0)
                            continue;
                        var file = ModListExporter.MatchFile(files, fileName, null);
                        found = new DeclaredSource("nexusmods", nexusId, domain, file?.FileId);
                        method = file is null ? "Nexus (mod found; that file is no longer listed)" : "Nexus";
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        method = $"Nexus lookup failed: {ex.Message}";
                    }
                }
            }

            if (found is null)
            {
                results.Add(new RecoveredSource(mod.ModId, name, null, method.Length > 0 ? method : "no source found (installed from a local file?)", archive));
                continue;
            }
            if (existing is not null && existing.FileId is null && found.FileId is null && found.ModId == existing.ModId)
                continue; // nothing new
            if (apply)
            {
                try
                {
                    DependencyConventions.SetRecordSource(archive, found);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    results.Add(new RecoveredSource(mod.ModId, name, null, $"found ({method}) but the record couldn't be written: {ex.Message}", archive));
                    continue;
                }
            }
            results.Add(new RecoveredSource(mod.ModId, name, found, method, archive));
        }
        return results;
    }

    private static string? ReleasedFileName(string archive)
    {
        var record = archive + ".modular.json";
        if (!File.Exists(record))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(record));
            return doc.RootElement.TryGetProperty("Released", out var r) && r.ValueKind == JsonValueKind.Object &&
                   r.TryGetProperty("FileName", out var f) ? f.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool LooksLikeModIo(string modId, string archive, string fileName) =>
        modId.StartsWith("modio-", StringComparison.OrdinalIgnoreCase) || NumberedModIo(fileName) is not null
        || archive.Replace('\\', '/').Contains("/modio/", StringComparison.OrdinalIgnoreCase);

    private static string? NumberedModIo(string fileName) =>
        System.Text.RegularExpressions.Regex.Match(fileName, @"^modio-(\d+)-") is { Success: true } m ? m.Groups[1].Value : null;

    private static string? NexusNumber(string modId) =>
        System.Text.RegularExpressions.Regex.Match(modId, @"^(\d+)(?:-[\w-]+)?$") is { Success: true } m ? m.Groups[1].Value : null;
}
