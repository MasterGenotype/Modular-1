using System.Text.Json;
using System.Text.RegularExpressions;
using Modular.Core.Database;
using Modular.Core.Installers;
using Modular.Core.Utilities;
using Modular.Sdk.Modules;

namespace Modular.Core.ModLists;

/// <summary>Where an installed mod came from on a mod site, remembered so update checks stay quick.</summary>
public sealed record ModSourceInfo(string Backend, string Game, string RemoteModId, string? FileId, string? FileName);

/// <summary>Per game, the resolved source of each installed mod (<c>~/.config/Modular/sources/&lt;game&gt;.json</c>).</summary>
public sealed class ModSourceCache(string? root = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string Root { get; } = root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "Modular", "sources");

    private string PathFor(string gameKey) => Path.Combine(Root, FileUtils.SanitizeDirectoryName(gameKey) + ".json");

    public Dictionary<string, ModSourceInfo> Load(string gameKey)
    {
        var path = PathFor(gameKey);
        if (!File.Exists(path))
            return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            return new(JsonSerializer.Deserialize<Dictionary<string, ModSourceInfo>>(File.ReadAllText(path), Json) ?? new(),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Save(string gameKey, Dictionary<string, ModSourceInfo> sources)
    {
        Directory.CreateDirectory(Root);
        var tmp = PathFor(gameKey) + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(sources, Json));
        File.Move(tmp, PathFor(gameKey), overwrite: true);
    }
}

/// <summary>A newer file for an installed mod.</summary>
public sealed record ModUpdate(string ModId, string Name, bool Enabled, ModSourceInfo Source, RemoteModFile? Current, RemoteModFile Newer, string? Note)
{
    /// <summary>The installed version: from the installed file, else from its download name.</summary>
    public string InstalledVersion => Current?.Version
        ?? ModUpdateService.VersionFromName(Source.FileName) ?? "installed file";

    public string Describe =>
        $"{Name}: {InstalledVersion} → {Newer.Version ?? Newer.FileName}" +
        (Newer.UploadedAt is { } at ? $" ({at.LocalDateTime:yyyy-MM-dd})" : "") + (Note is { } n ? $" — {n}" : "");
}

public sealed record ModUpdateCheck(IReadOnlyList<ModUpdate> Updates, int Checked, int NoSource);

public sealed record ModUpdateResult(int Updated, IReadOnlyList<(ModUpdate Update, string Error)> Failed);

/// <summary>
/// Finds newer files for a game's installed mods (Nexus, mod.io) and installs them in place: same
/// mod id, same place in the install and load order, same enabled state; one deploy at the end.
/// </summary>
public sealed partial class ModUpdateService
{
    private readonly ModularDatabase _db;
    private readonly ModInstallationService _install;
    private readonly ModListLookups _lookups;
    private readonly ModSourceCache _cache;

    public ModUpdateService(ModularDatabase db, ModInstallationService install, ModListLookups lookups, ModSourceCache? cache = null)
    {
        _db = db;
        _install = install;
        _lookups = lookups;
        _cache = cache ?? new ModSourceCache();
    }

    public async Task<ModUpdateCheck> CheckAsync(string gameKey, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var mods = await _install.Deployment.Mods.ListAsync(gameKey, ct: ct);
        var sources = _cache.Load(gameKey);

        // First check of a game: work out every mod's source once (slow), then remember it.
        if (mods.Any(m => !sources.ContainsKey(m.ModId)))
        {
            progress?.Report("Working out where each mod came from (first check of this game; later checks are quicker)…");
            var list = await new ModListExporter(_db).ExportAsync(gameKey, null, null, _lookups,
                new Progress<string>(n => progress?.Report($"Looking up {n}…")), ct);
            foreach (var e in list.Mods.Where(e => e.Source is not null))
                sources[e.ModId] = new ModSourceInfo(e.Source!.Backend, e.Source.Game, e.Source.ModId, e.Source.FileId, e.ArchiveName);
            _cache.Save(gameKey, sources);
        }

        var updates = new List<ModUpdate>();
        var checkedCount = 0;
        var noSource = 0;
        foreach (var mod in mods)
        {
            ct.ThrowIfCancellationRequested();
            if (!sources.TryGetValue(mod.ModId, out var src))
            {
                noSource++;
                continue;
            }
            var name = string.IsNullOrWhiteSpace(mod.DisplayName) ? mod.ModId : mod.DisplayName!;
            progress?.Report($"Checking {name}…");
            IReadOnlyList<RemoteModFile> files;
            try
            {
                files = await _lookups.ListFilesAsync(src.Backend, src.Game, src.RemoteModId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue;
            }
            checkedCount++;
            if (FindNewer(files, src) is { } found)
                updates.Add(new ModUpdate(mod.ModId, name, mod.Enabled, src, found.Current, found.Newer, found.Note));
        }
        return new ModUpdateCheck(updates, checkedCount, noSource);
    }

    /// <summary>
    /// The newer file in the same series as the installed one: same category, same name apart
    /// from version and date, uploaded later. When the installed file is no longer listed, the
    /// newest main file.
    /// </summary>
    public static (RemoteModFile? Current, RemoteModFile Newer, string? Note)? FindNewer(IReadOnlyList<RemoteModFile> files, ModSourceInfo src)
    {
        var listed = files.Where(f => f.UploadedAt is not null && !string.Equals(f.Category, "archived", StringComparison.OrdinalIgnoreCase)
                                      && !string.Equals(f.Category, "old_version", StringComparison.OrdinalIgnoreCase)).ToList();
        var current = files.FirstOrDefault(f => src.FileId is not null && f.FileId == src.FileId)
            ?? (src.FileName is null ? null : ModListExporter.MatchFile(files, src.FileName, null));
        if (current is null && src.FileName is not null && ModListExporter.UploadInfoFromName(src.FileName) is { Uploaded: { } installedAt })
        {
            // The installed file was taken off the page, but its download name says when it was
            // uploaded: anything later in the same series is newer.
            var series0 = Series(src.FileName);
            var later = listed
                .Where(f => f.UploadedAt > installedAt.AddMinutes(2) && Series(f.FileName) == series0)
                .OrderByDescending(f => f.UploadedAt)
                .FirstOrDefault();
            return later is null ? null : (null, later, "the installed version was taken off the mod page");
        }
        if (current is null)
        {
            if (src.FileId is null)
                return null; // never knew the installed file: can't tell what's newer
            var main = listed.Where(f => string.Equals(f.Category, "main", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.UploadedAt).FirstOrDefault();
            return main is null ? null : (null, main, "the installed file is no longer on the mod page; this is its newest main file");
        }

        var series = Series(current.FileName);
        var newer = listed
            .Where(f => f.FileId != current.FileId && f.UploadedAt > current.UploadedAt
                        && string.Equals(f.Category ?? "", current.Category ?? "", StringComparison.OrdinalIgnoreCase)
                        && Series(f.FileName) == series)
            .OrderByDescending(f => f.UploadedAt)
            .FirstOrDefault();
        return newer is null ? null : (current, newer, null);
    }

    /// <summary>The version in a Nexus download name: "Mod-3850-3-2-1-1777741508" → "3.2.1"; "Mod 3850 4.1.8 2026-…Z x" → "4.1.8".</summary>
    public static string? VersionFromName(string? fileName)
    {
        if (fileName is null)
            return null;
        if (ModListExporter.UploadInfoFromName(fileName).Version is { } v)
            return v;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var m = Regex.Match(stem, @"-\d+-((?:[\w]+-)*?[\w]+)-\d{9,11}$");
        return m.Success ? m.Groups[1].Value.Replace('-', '.') : null;
    }

    /// <summary>The folder the mod's current archive (or its record) is in, when that is inside the mods folder.</summary>
    private async Task<string?> PreviousArchiveFolderAsync(string gameKey, string modId, string modsRoot, CancellationToken ct)
    {
        var row = await _install.Deployment.Mods.GetAsync(gameKey, modId, ct);
        var archive = row?.ChangesetId is { } cs ? (await new ChangesetManager(_db).GetChangesetAsync(cs, ct))?.ArchivePath : null;
        var dir = archive is null ? null : Path.GetDirectoryName(archive);
        if (dir is null || !Path.GetFullPath(dir).StartsWith(Path.GetFullPath(modsRoot), StringComparison.Ordinal))
            return null;
        // Files Modular keeps for installed mods (.archives) are not where downloads go.
        return dir.Contains($"{Path.DirectorySeparatorChar}.archives{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? null : dir;
    }

    /// <summary>A file name without version numbers, dates, ids or extension: "Mod Name-123-1-2-1700000000.zip" → "mod name".</summary>
    internal static string Series(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        stem = NewStyleTail().Replace(stem, "");
        stem = OldStyleTail().Replace(stem, "");
        stem = VersionWords().Replace(stem.Replace('_', ' '), " ");
        return Spaces().Replace(stem.Replace('-', ' '), " ").Trim();
    }

    [GeneratedRegex(@"\s\d+\s\S+\s\d{4}-\d{2}-\d{2}t\d{2}-\d{2}z\s\w+$")]
    private static partial Regex NewStyleTail();

    [GeneratedRegex(@"-\d+(?:-[\w.]+)*-\d{9,11}$")]
    private static partial Regex OldStyleTail();

    [GeneratedRegex(@"\bv?\d+(?:[._]\d+)+\w*\b|\bv\d+\b")]
    private static partial Regex VersionWords();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>Downloads and installs the chosen updates in place, then deploys once.</summary>
    /// <param name="choiceResolver">Asks about installer options a mod hasn't saved answers for (or that are new in
    /// this version). Without one, such a mod is not updated rather than installed with defaults.</param>
    public async Task<ModUpdateResult> ApplyAsync(
        string gameKey, GameInstallation installation, IReadOnlyList<ModUpdate> updates, HttpClient http, string modsRoot,
        IProgress<string>? progress = null, CancellationToken ct = default,
        Func<IReadOnlyList<Modular.Sdk.Installers.InstallChoice>, CancellationToken, Task<IReadOnlyDictionary<string, string>?>>? choiceResolver = null)
    {
        var sources = _cache.Load(gameKey);
        var failed = new List<(ModUpdate, string)>();
        var done = 0;
        foreach (var u in updates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                progress?.Report($"Downloading {u.Name}…");
                var target = new ModListSource(u.Source.Backend, u.Source.Game, u.Source.RemoteModId, u.Newer.FileId);
                var url = await _lookups.ResolveUrlAsync(target, ct);
                if (url is null)
                {
                    failed.Add((u, u.Source.Backend == "nexusmods"
                        ? $"no download link (Nexus gives API downloads to Premium accounts only); get it from {target.PageUrl}"
                        : "no download link"));
                    continue;
                }
                // Beside the version it replaces, which is already where the library keeps it
                // (its category folder); a mod with no earlier archive falls back to the layout.
                var folder = await PreviousArchiveFolderAsync(gameKey, u.ModId, modsRoot, ct)
                    ?? ModLibraryLayout.ModFolder(modsRoot, u.Source.Backend, u.Source.Game, u.Name);
                Directory.CreateDirectory(folder);
                var archive = Path.Combine(folder, FileUtils.SanitizeDirectoryName(u.Newer.FileName));
                if (!File.Exists(archive))
                {
                    var tmp = archive + ".part";
                    await using (var s = await http.GetStreamAsync(url, ct))
                    await using (var f = File.Create(tmp))
                        await s.CopyToAsync(f, ct);
                    File.Move(tmp, archive, overwrite: true);
                }

                progress?.Report($"Installing {u.Name}…");
                var result = await _install.InstallAsync(archive, installation.InstallPath, new ModInstallationOptions
                {
                    ModId = u.ModId,
                    DisplayName = u.Name,
                    GameId = gameKey,
                    Source = u.Source.Backend,
                    SourceFileId = u.Newer.FileId,
                    NoDeploy = true,
                    ChoiceResolver = choiceResolver,
                    RequireAnsweredChoices = choiceResolver is null,
                }, ct: ct);
                if (!result.Success)
                {
                    failed.Add((u, result.Error ?? "install failed"));
                    continue;
                }
                if (!u.Enabled)
                    await _install.Deployment.Mods.SetEnabledAsync(gameKey, u.ModId, false, ct);
                sources[u.ModId] = u.Source with { FileId = u.Newer.FileId, FileName = u.Newer.FileName };
                done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.Add((u, ex.Message));
            }
        }
        _cache.Save(gameKey, sources);
        if (done > 0)
        {
            progress?.Report("Deploying…");
            await _install.Deployment.DeployAsync(installation, ct: ct);
        }
        return new ModUpdateResult(done, failed);
    }
}
