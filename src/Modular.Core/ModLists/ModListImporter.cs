using Modular.Core.Installers;
using Modular.Core.Utilities;
using Modular.Sdk.Modules;

namespace Modular.Core.ModLists;

/// <summary>What importing a list will do with one of its mods.</summary>
public enum ModListAction
{
    /// <summary>Installed already (same mod id): left as it is.</summary>
    AlreadyInstalled,
    /// <summary>Downloaded (exact file) and installed.</summary>
    Download,
    /// <summary>The mod page is known but not the file: the user picks it.</summary>
    PickFile,
    /// <summary>No download source: the user supplies the archive.</summary>
    Manual,
}

public sealed record ModListPlanItem(ModListEntry Entry, ModListAction Action, string Detail);

public sealed record ModListImportResult(int Installed, int Skipped, IReadOnlyList<(ModListEntry Entry, string Error)> Failed, IReadOnlyList<ModListPlanItem> NeedsUser);

/// <summary>
/// Installs a <see cref="ModList"/> into a game: downloads each exact file, installs them in the
/// list's order without deploying, applies enabled/disabled, then deploys once.
/// </summary>
public sealed class ModListImporter
{
    private readonly ModInstallationService _install;

    public ModListImporter(ModInstallationService install) => _install = install;

    public async Task<IReadOnlyList<ModListPlanItem>> PlanAsync(ModList list, string gameKey, CancellationToken ct = default)
    {
        var installed = (await _install.Deployment.Mods.ListAsync(gameKey, ct: ct))
            .Select(m => m.ModId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return list.Mods.OrderBy(m => m.Order).Select(entry =>
        {
            if (installed.Contains(entry.ModId))
                return new ModListPlanItem(entry, ModListAction.AlreadyInstalled, "already installed");
            if (entry.Source is { FileId: { } file } src)
                return new ModListPlanItem(entry, ModListAction.Download, $"{src.Backend} {src.Game} mod {src.ModId}, file {file}");
            if (entry.Source is { } page)
                return new ModListPlanItem(entry, ModListAction.PickFile,
                    (page.PageUrl ?? $"{page.Backend} mod {page.ModId}") + (entry.Note is { } n ? $" — {n}" : ""));
            return new ModListPlanItem(entry, ModListAction.Manual, entry.ArchiveName is { } a ? $"supply {a}" : "supply the archive");
        }).ToList();
    }

    /// <summary>A warning when the list was made on another build of the game than the one installed; null when they match or either is unknown.</summary>
    public static string? BuildMismatch(ModList list, string gameKey)
    {
        if (list.Game.Build is not { } listed || !uint.TryParse(gameKey, out var appId) || Modular.Core.Depots.BuildStamp.ForApp(appId) is not { } current)
            return null;
        return listed.SameBuildAs(current) ? null
            : $"This list was made on {listed.Label}; this game is on {current.Label}. Mods may not work on a different build.";
    }

    /// <param name="resolveUrl">Turns a source into a download URL (Nexus needs Premium for this).</param>
    /// <param name="modsRoot">Modular's mods folder; downloads land in its usual layout.</param>
    public async Task<ModListImportResult> ApplyAsync(
        ModList list, GameInstallation installation, string gameKey,
        Func<ModListSource, CancellationToken, Task<string?>> resolveUrl, HttpClient http, string modsRoot,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var plan = await PlanAsync(list, gameKey, ct);
        var failed = new List<(ModListEntry, string)>();
        var installedCount = 0;
        foreach (var item in plan.Where(p => p.Action == ModListAction.Download))
        {
            ct.ThrowIfCancellationRequested();
            var entry = item.Entry;
            var src = entry.Source!;
            try
            {
                progress?.Report($"Downloading {entry.Name}…");
                var url = await resolveUrl(src, ct);
                if (url is null)
                {
                    failed.Add((entry, $"no download link (Nexus gives API downloads to Premium accounts only); get it from {src.PageUrl}"));
                    continue;
                }
                var folder = ModLibraryLayout.ModFolder(modsRoot, src.Backend, src.Game, entry.Name);
                Directory.CreateDirectory(folder);
                var fileName = entry.ArchiveName ?? Path.GetFileName(new Uri(url).LocalPath);
                var archive = Path.Combine(folder, FileUtils.SanitizeDirectoryName(fileName));
                if (!File.Exists(archive) || (entry.Size is { } size && new FileInfo(archive).Length != size))
                {
                    var tmp = archive + ".part";
                    await using (var s = await http.GetStreamAsync(url, ct))
                    await using (var f = File.Create(tmp))
                        await s.CopyToAsync(f, ct);
                    File.Move(tmp, archive, overwrite: true);
                }

                progress?.Report($"Installing {entry.Name}…");
                var result = await _install.InstallAsync(archive, installation.InstallPath, new ModInstallationOptions
                {
                    ModId = entry.ModId,
                    DisplayName = entry.Name,
                    GameId = gameKey,
                    Source = src.Backend,
                    SourceFileId = src.FileId,
                    NoDeploy = true,
                }, ct: ct);
                if (!result.Success)
                {
                    failed.Add((entry, result.Error ?? "install failed"));
                    continue;
                }
                if (!entry.Enabled)
                    await _install.Deployment.Mods.SetEnabledAsync(gameKey, entry.ModId, false, ct);
                installedCount++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.Add((entry, ex.Message));
            }
        }

        if (installedCount > 0)
        {
            progress?.Report("Applying the list's order and deploying…");
            await _install.Deployment.Mods.SetOrderAsync(gameKey, list.Mods.OrderBy(m => m.Order).Select(m => m.ModId).ToList(), ct);
            await _install.Deployment.DeployAsync(installation, ct: ct);
        }
        return new ModListImportResult(installedCount,
            plan.Count(p => p.Action == ModListAction.AlreadyInstalled), failed,
            plan.Where(p => p.Action is ModListAction.PickFile or ModListAction.Manual).ToList());
    }
}
