using Modular.Core.Backends.ModIo;
using Modular.Core.Backends.NexusMods;
using Modular.Sdk.Backends;

namespace Modular.Core.ModLists;

/// <summary>A mod found on a site: its id there, the site's game id, and its name.</summary>
public sealed record RemoteMod(string ModId, string Game, string Name);

/// <summary>
/// The online lookups a mod list needs, per site: list a mod's files (to pin the exact file),
/// find a mod.io mod by its name id or title, and turn a source into a download URL.
/// </summary>
public sealed class ModListLookups
{
    private readonly NexusModsBackend? _nexus;
    private readonly ModIoBackend? _modio;

    public ModListLookups(NexusModsBackend? nexus, ModIoBackend? modio)
    {
        _nexus = nexus;
        _modio = modio;
    }

    public bool HasNexus => _nexus is not null;
    public bool HasModIo => _modio is not null;

    public async Task<IReadOnlyList<RemoteModFile>> ListFilesAsync(string backend, string? game, string modId, CancellationToken ct)
    {
        var files = backend switch
        {
            "nexusmods" when _nexus is not null => await _nexus.GetModFilesAsync(modId, game, ct: ct),
            "modio" when _modio is not null => await _modio.GetModFilesAsync(modId, string.IsNullOrEmpty(game) ? null : game, ct: ct),
            _ => [],
        };
        return files.Select(f => new RemoteModFile(f.FileId, f.FileName, f.SizeBytes, f.Version,
            f.UploadedAt is { } u ? new DateTimeOffset(DateTime.SpecifyKind(u, DateTimeKind.Utc)) : null, f.Category?.ToLowerInvariant())).ToList();
    }

    /// <summary>A mod.io mod by its name id (the URL slug), else by exact title.</summary>
    public async Task<RemoteMod?> FindModIoAsync(string? nameId, string? title, CancellationToken ct)
    {
        if (_modio is null)
            return null;
        string? id = null;
        if (!string.IsNullOrWhiteSpace(nameId))
            id = await _modio.FindModIdAsync(nameId, null, ct);
        if (id is null && !string.IsNullOrWhiteSpace(title))
        {
            var found = await _modio.SearchModsAsync(new ModSearchQuery { Terms = title, AdultContent = null, PageSize = 20 }, ct);
            id = found.Mods.FirstOrDefault(m => string.Equals(m.Name.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase))?.ModId;
        }
        if (id is null)
            return null;
        var info = await _modio.GetModInfoAsync(id, null, ct);
        return new RemoteMod(id, info?.GameDomain ?? "", info?.Name ?? title ?? nameId ?? id);
    }

    public Task<string?> ResolveUrlAsync(ModListSource src, CancellationToken ct) => src.Backend switch
    {
        "nexusmods" when _nexus is not null => _nexus.ResolveDownloadUrlAsync(src.ModId, src.FileId!, src.Game, ct),
        "modio" when _modio is not null => _modio.ResolveDownloadUrlAsync(src.ModId, src.FileId!, string.IsNullOrEmpty(src.Game) ? null : src.Game, ct),
        _ => Task.FromResult<string?>(null),
    };
}
