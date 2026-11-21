using Modular.Core.Database;
using Modular.Core.Deployment;
using Modular.Core.Modules;

namespace Modular.Core.Installers;

/// <summary>A listed name that is only a mod number, and what it can be replaced with.</summary>
public sealed record ModNameFix(string GameKey, string ModId, string? NewName, string Source);

/// <summary>
/// Finds installed mods listed under their bare mod number (installs by <c>--mod-id</c> before
/// names were resolved) and names them: metadata cache, then an optional online lookup (Nexus
/// API), then the archive's file name.
/// </summary>
public sealed class ModNameRepair
{
    private readonly ModularDatabase _db;
    private readonly ModMetadataCache? _cache;
    private readonly GameModuleRegistry _modules;

    public ModNameRepair(ModularDatabase db, ModMetadataCache? cache, GameModuleRegistry? modules = null)
    {
        _db = db;
        _cache = cache;
        _modules = modules ?? new GameModuleRegistry();
    }

    /// <summary>Nexus domains a game key's mods can come from (its module's game ids).</summary>
    public IReadOnlyList<string> DomainsFor(string gameKey)
    {
        var module = int.TryParse(gameKey, out var appId) ? _modules.ResolveByAppId(appId) : _modules.ResolveByGameId(gameKey);
        if (module is null)
            return [gameKey];
        return module.GameIds.Append(module.ModuleId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <param name="online">Looks a mod up by (domain, id); null skips the network.</param>
    /// <param name="apply">Write the names; otherwise only report them.</param>
    public async Task<IReadOnlyList<ModNameFix>> RepairAsync(
        string? gameKey, Func<string, int, CancellationToken, Task<string?>>? online, bool apply, CancellationToken ct = default)
    {
        var repo = new GameModRepository(_db);
        var changesets = new ChangesetManager(_db);
        var keys = gameKey is not null ? [gameKey] : await repo.ListGameKeysAsync(ct);
        var fixes = new List<ModNameFix>();
        foreach (var key in keys)
        {
            var domains = DomainsFor(key);
            foreach (var mod in await repo.ListAsync(key, ct: ct))
            {
                var shown = string.IsNullOrWhiteSpace(mod.DisplayName) ? mod.ModId : mod.DisplayName;
                // "24893-2": the second file installed from mod 24893.
                var parts = shown.Split('-');
                if (!(ModDisplayNames.IsNumericId(shown) || (parts.Length == 2 && parts.All(ModDisplayNames.IsNumericId))))
                    continue;
                var listed = parts[0];
                var fileNumber = parts.Length == 2 ? parts[1] : null;

                var archive = mod.ChangesetId is { } cs ? (await changesets.GetChangesetAsync(cs, ct))?.ArchivePath : null;
                string? name = null;
                var source = "";
                if (_cache is not null && int.TryParse(listed, out var id))
                {
                    name = domains.Select(d => _cache.GetModMetadata(d, id)?.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
                    source = name is null ? "" : "metadata cache";
                }
                if (name is null && online is not null && int.TryParse(listed, out var onlineId))
                {
                    foreach (var d in domains)
                    {
                        name = await online(d, onlineId, ct);
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            source = $"{d} online";
                            break;
                        }
                    }
                }
                if (name is null && archive is not null)
                {
                    name = ModDisplayNames.FromArchiveFileName(archive, listed);
                    source = name is null ? "" : "archive name";
                }
                if (name is not null && fileNumber is not null)
                {
                    // Tell the files apart by their archive name, else by number.
                    var file = archive is null ? null
                        : ModDisplayNames.FromArchiveFileName(archive, listed) ?? Path.GetFileNameWithoutExtension(archive);
                    name = $"{name} ({(string.IsNullOrWhiteSpace(file) ? $"file {fileNumber}" : file)})";
                }
                fixes.Add(new ModNameFix(key, mod.ModId, name, name is null ? "not found" : source));
                if (apply && name is not null)
                    await repo.SetDisplayNameAsync(key, mod.ModId, name, ct);
            }
        }
        return fixes;
    }
}
