using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Modular.Core.Deployment;
using Modular.Core.Installers.BaldursGate3;
using Modular.Sdk.Modules;

namespace Modular.Core.InGame;

/// <summary>
/// Keeps Modular's records of a game's own mod manager in step with what the game has
/// downloaded, so that a subscription is installed exactly once.
/// </summary>
/// <remarks>
/// <para>
/// Subscribing hands the download to the game: Modular records the mod as waiting and
/// writes nothing to the game's folders. After the game has run, <see cref="SyncAsync"/>
/// finds the file the game made and records it. Modular never places a file the game would
/// download itself, since a copy the game does not recognise as its own is downloaded again.
/// </para>
/// <para>
/// A game-owned mod is never deployed: it has no row in <c>game_mod</c>, so the deployer
/// can neither rename nor remove its file.
/// </para>
/// </remarks>
public sealed class InGameModService
{
    private readonly GameOwnedModRepository _owned;
    private readonly GameModRepository _mods;
    private readonly IInGameModManager _manager;
    private readonly ILogger? _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="owned">Game-owned mod records.</param>
    /// <param name="mods">Modular's own deployed mods.</param>
    /// <param name="manager">The game's mod manager.</param>
    /// <param name="logger">Optional logger.</param>
    public InGameModService(
        GameOwnedModRepository owned,
        GameModRepository mods,
        IInGameModManager manager,
        ILogger? logger = null)
    {
        _owned = owned;
        _mods = mods;
        _manager = manager;
        _logger = logger;
    }

    /// <summary>The game's mod manager.</summary>
    public IInGameModManager Manager => _manager;

    /// <summary>
    /// Finds Modular's own mods that would duplicate a subscription: the same hub mod, or
    /// the same game module, deployed by Modular under its own file name.
    /// </summary>
    /// <param name="installation">The game.</param>
    /// <param name="remoteModId">The hub's id for the mod.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The Modular mods, with the module each provides.</returns>
    public async Task<IReadOnlyList<ModularCopy>> FindModularCopiesAsync(
        GameInstallation installation, string remoteModId, CancellationToken ct = default)
    {
        var files = _manager.Scan(installation);
        var modules = files
            .Where(f => f.RemoteModId == remoteModId)
            .Select(f => f.ModuleUuid)
            .ToHashSet(StringComparer.Ordinal);
        var known = await _owned.GetAsync(installation.GameKey, _manager.BackendId, remoteModId, ct);
        if (known?.ModuleUuid != null)
            modules.Add(known.ModuleUuid);

        if (modules.Count == 0)
            return [];

        var copies = new List<ModularCopy>();
        foreach (var mod in await _mods.ListAsync(installation.GameKey, enabledOnly: false, ct))
        {
            foreach (var uuid in StagedModules(mod.StagingPath).Where(modules.Contains))
                copies.Add(new ModularCopy(mod.ModId, mod.DisplayName ?? mod.ModId, uuid, mod.Enabled));
        }

        return copies;
    }

    /// <summary>
    /// Records a new subscription as waiting for the game to download it.
    /// </summary>
    /// <param name="installation">The game.</param>
    /// <param name="remoteModId">The hub's id for the mod.</param>
    /// <param name="displayName">The mod's name.</param>
    /// <param name="remoteFileId">The hub's id for the mod's live file, when known.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The record.</returns>
    /// <remarks>
    /// Nothing is written to the game's folders. A mod the game has already downloaded is
    /// recorded as installed instead.
    /// </remarks>
    public async Task<GameOwnedModRecord> RecordSubscriptionAsync(
        GameInstallation installation,
        string remoteModId,
        string? displayName,
        string? remoteFileId,
        CancellationToken ct = default)
    {
        var existing = await _owned.GetAsync(installation.GameKey, _manager.BackendId, remoteModId, ct);
        var record = existing ?? new GameOwnedModRecord
        {
            GameKey = installation.GameKey,
            BackendId = _manager.BackendId,
            RemoteModId = remoteModId,
            RemoteGameId = _manager.RemoteGameId,
            State = GameOwnedModState.PendingDownload,
        };

        record = record with
        {
            DisplayName = displayName ?? record.DisplayName,
            RemoteFileId = remoteFileId ?? record.RemoteFileId,
        };

        var file = PickGameFile(_manager.Scan(installation), record);
        if (file != null)
            record = Adopt(record, file);

        await _owned.UpsertAsync(record, ct);
        return record;
    }

    /// <summary>
    /// Removes a game-owned mod after its subscription has ended: its file, its load-order
    /// entry and Modular's record.
    /// </summary>
    /// <param name="installation">The game.</param>
    /// <param name="remoteModId">The hub's id for the mod.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What was removed; empty when Modular knew nothing of the mod and it has no file.</returns>
    /// <exception cref="InvalidOperationException">The game is running.</exception>
    public async Task<IReadOnlyList<string>> RemoveAsync(
        GameInstallation installation, string remoteModId, CancellationToken ct = default)
    {
        if (_manager.IsGameRunning())
            throw new InvalidOperationException("The game is running; close it before removing mods.");

        var record = await _owned.GetAsync(installation.GameKey, _manager.BackendId, remoteModId, ct);
        var removed = new List<string>();

        // The game's own files for the mod: never one Modular deployed under its own name.
        foreach (var file in _manager.Scan(installation)
                     .Where(f => !f.HasModularName &&
                                 (f.RemoteModId == remoteModId ||
                                  (record?.FileName != null && f.FileName == record.FileName))))
        {
            removed.AddRange(_manager.Remove(installation, file));
        }

        if (record != null)
            await _owned.RemoveAsync(installation.GameKey, _manager.BackendId, remoteModId, ct);

        return removed;
    }

    /// <summary>
    /// Brings Modular's records in line with the game's mods folder and the subscriptions.
    /// Writes only to Modular's own records; the game's files are left as they are.
    /// </summary>
    /// <param name="installation">The game.</param>
    /// <param name="subscribed">The user's subscriptions on the hub, by mod id.</param>
    /// <param name="names">Mod names by id, for reporting; may be partial.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What was found and changed.</returns>
    public async Task<InGameSyncReport> SyncAsync(
        GameInstallation installation,
        IReadOnlySet<string> subscribed,
        IReadOnlyDictionary<string, string>? names = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subscribed);

        var files = _manager.Scan(installation);
        var records = (await _owned.ListAsync(installation.GameKey, ct))
            .Where(r => r.BackendId == _manager.BackendId)
            .ToDictionary(r => r.RemoteModId, StringComparer.Ordinal);
        var items = new List<InGameSyncItem>();
        string NameOf(string id, GameOwnedModRecord? r = null) =>
            r?.DisplayName ?? (names != null && names.TryGetValue(id, out var n) ? n : null) ?? $"mod {id}";

        foreach (var record in records.Values)
        {
            var file = PickGameFile(files, record);
            var isSubscribed = subscribed.Contains(record.RemoteModId);
            var name = NameOf(record.RemoteModId, record);

            if (file == null)
            {
                if (!isSubscribed)
                {
                    await _owned.RemoveAsync(record.GameKey, record.BackendId, record.RemoteModId, ct);
                    items.Add(new(InGameSyncKind.Forgotten, record.RemoteModId, name, null,
                        "no longer subscribed and its file is gone"));
                }
                else if (record.State == GameOwnedModState.Installed)
                {
                    await _owned.UpsertAsync(record with { State = GameOwnedModState.PendingDownload }, ct);
                    items.Add(new(InGameSyncKind.AwaitingDownload, record.RemoteModId, name, null,
                        $"its file {record.FileName} is gone; the game downloads it again on its next launch"));
                }
                else
                {
                    items.Add(new(InGameSyncKind.AwaitingDownload, record.RemoteModId, name, null,
                        "the game downloads it on its next launch"));
                }

                continue;
            }

            if (record.State == GameOwnedModState.PendingDownload)
            {
                await _owned.UpsertAsync(Adopt(record, file), ct);
                items.Add(new(InGameSyncKind.Adopted, record.RemoteModId, name, file.FileName,
                    $"downloaded by the game, version {FormatVersion(file.Version64)}"));
            }
            else if (record.FileName != file.FileName ||
                     record.Version64 != file.Version64 ||
                     !string.Equals(record.UploadMd5, file.UploadMd5, StringComparison.OrdinalIgnoreCase))
            {
                await _owned.UpsertAsync(Adopt(record, file), ct);
                items.Add(new(InGameSyncKind.UpdatedByGame, record.RemoteModId, name, file.FileName,
                    $"updated by the game: {FormatVersion(record.Version64)} → {FormatVersion(file.Version64)}" +
                    (record.FileName != file.FileName ? $" (was {record.FileName})" : "")));
            }

            if (!isSubscribed)
            {
                items.Add(new(InGameSyncKind.Unsubscribed, record.RemoteModId, name, file.FileName,
                    "no longer subscribed; the game may delete it. Untrack it to remove it now"));
            }
        }

        // Subscriptions made outside Modular, on the website or in the game.
        foreach (var id in subscribed.Where(id => !records.ContainsKey(id)).Order(StringComparer.Ordinal))
        {
            var record = await RecordSubscriptionAsync(installation, id, NameOf(id), null, ct);
            items.Add(record.State == GameOwnedModState.Installed
                ? new(InGameSyncKind.Adopted, id, NameOf(id), record.FileName, "subscribed outside Modular; already downloaded")
                : new(InGameSyncKind.AwaitingDownload, id, NameOf(id), null,
                    "subscribed outside Modular; the game downloads it on its next launch"));
        }

        foreach (var file in files.Where(f => f.RemoteModId != null && !f.HasModularName &&
                                              !records.ContainsKey(f.RemoteModId) &&
                                              !subscribed.Contains(f.RemoteModId)))
        {
            items.Add(new(InGameSyncKind.Unsubscribed, file.RemoteModId!, file.Name, file.FileName,
                "a game download for a mod that is not subscribed; left alone"));
        }

        foreach (var file in files.Where(f => f.HasModularName && f.RemoteModId != null && subscribed.Contains(f.RemoteModId)))
        {
            items.Add(new(InGameSyncKind.ModularCopy, file.RemoteModId!, file.Name, file.FileName,
                "Modular deployed this subscribed mod under its own name; the game will add a second copy"));
        }

        foreach (var group in files.GroupBy(f => f.ModuleUuid).Where(g => g.Count() > 1))
        {
            var first = group.First();
            items.Add(new(InGameSyncKind.Duplicate, first.RemoteModId ?? first.ModuleUuid, first.Name, null,
                $"{group.Count()} files hold the same module: {string.Join(", ", group.Select(f => f.FileName))}"));
        }

        _logger?.LogInformation("In-game sync for {Game}: {Count} findings", installation.GameKey, items.Count);
        return new InGameSyncReport(items);
    }

    /// <summary>Formats a packed BG3 version as major.minor.revision.build.</summary>
    /// <param name="version64">Packed version, or null.</param>
    /// <returns>The version, or "?".</returns>
    public static string FormatVersion(long? version64) => version64 is not { } v
        ? "?"
        : string.Create(CultureInfo.InvariantCulture,
            $"{v >> 55}.{(v >> 47) & 0xFF}.{(v >> 31) & 0xFFFF}.{v & 0x7FFFFFFF}");

    // The game's file for a record: the one it was last seen as, else the game's own file
    // naming the mod. Never a file Modular deployed under its own name.
    private static InGameModFile? PickGameFile(IReadOnlyList<InGameModFile> files, GameOwnedModRecord record) =>
        files.FirstOrDefault(f => !f.HasModularName && record.FileName != null && f.FileName == record.FileName &&
                                  (f.RemoteModId == null || f.RemoteModId == record.RemoteModId))
        ?? files.Where(f => !f.HasModularName && f.RemoteModId == record.RemoteModId)
            .OrderByDescending(f => f.Version64)
            .FirstOrDefault();

    private static GameOwnedModRecord Adopt(GameOwnedModRecord record, InGameModFile file) => record with
    {
        State = GameOwnedModState.Installed,
        FileName = file.FileName,
        ModuleUuid = file.ModuleUuid,
        Version64 = file.Version64,
        UploadMd5 = file.UploadMd5,
        FileMd5 = record.FileName == file.FileName && record.FileMd5 != null ? record.FileMd5 : HashFile(file.FullPath),
        DisplayName = record.DisplayName ?? file.Name,
    };

    private static string? HashFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexStringLower(MD5.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // The modules a Modular mod's staged paks contain.
    private IEnumerable<string> StagedModules(string stagingPath)
    {
        if (!Directory.Exists(stagingPath))
            yield break;

        foreach (var pak in Directory.EnumerateFiles(stagingPath, "*.pak", SearchOption.AllDirectories))
        {
            string? uuid = null;
            try
            {
                uuid = BG3PakMeta.Read(pak)?.Module.Uuid.ToLowerInvariant();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _logger?.LogDebug(ex, "Could not read staged pak {Pak}", pak);
            }

            if (uuid != null)
                yield return uuid;
        }
    }
}

/// <summary>A Modular-deployed mod providing the same module as a subscription.</summary>
/// <param name="ModId">Modular's id for the mod.</param>
/// <param name="Name">The mod's name.</param>
/// <param name="ModuleUuid">The shared module.</param>
/// <param name="Enabled">Whether Modular currently deploys it.</param>
public sealed record ModularCopy(string ModId, string Name, string ModuleUuid, bool Enabled);

/// <summary>What <see cref="InGameModService.SyncAsync"/> found.</summary>
public enum InGameSyncKind
{
    /// <summary>The game downloaded a subscribed mod, now recorded as installed.</summary>
    Adopted,

    /// <summary>The game replaced a mod's file with another upload.</summary>
    UpdatedByGame,

    /// <summary>Subscribed, with no file yet.</summary>
    AwaitingDownload,

    /// <summary>Neither subscribed nor present; the record was dropped.</summary>
    Forgotten,

    /// <summary>A game download for a mod that is no longer subscribed.</summary>
    Unsubscribed,

    /// <summary>A subscribed mod Modular also deployed under its own file name.</summary>
    ModularCopy,

    /// <summary>Several files hold the same module.</summary>
    Duplicate,
}

/// <summary>One finding.</summary>
/// <param name="Kind">What was found.</param>
/// <param name="RemoteModId">The hub's id for the mod, or the module UUID.</param>
/// <param name="Name">The mod's name.</param>
/// <param name="FileName">The file concerned, if any.</param>
/// <param name="Detail">A sentence for the user.</param>
public sealed record InGameSyncItem(
    InGameSyncKind Kind, string RemoteModId, string Name, string? FileName, string Detail);

/// <summary>The findings of one sync.</summary>
/// <param name="Items">Every finding.</param>
public sealed record InGameSyncReport(IReadOnlyList<InGameSyncItem> Items);
