using Modular.Core.Database;
using Modular.Core.InGame;
using Modular.Core.Installers;
using Modular.Core.Services;
using Modular.Sdk.Backends;
using Modular.Sdk.Modules;

namespace Modular.Gui.Services;

/// <summary>
/// Tracks and untracks mods for the GUI, the same way the CLI does: Baldur's Gate 3
/// mod.io subscriptions are handed to the game's own mod manager, never installed by
/// Modular as well.
/// </summary>
public sealed class TrackingCoordinator
{
    private readonly ModularDatabase _database;
    private readonly ModInstallationService _install;
    private readonly IDialogService _dialogs;
    private readonly ModTrackingService _tracking = new();

    /// <summary>Creates the coordinator.</summary>
    public TrackingCoordinator(ModularDatabase database, ModInstallationService install, IDialogService dialogs)
    {
        _database = database;
        _install = install;
        _dialogs = dialogs;
    }

    /// <summary>
    /// Tracks or untracks mods, asking first where the game is affected.
    /// </summary>
    /// <param name="backend">The backend.</param>
    /// <param name="mods">Mod ids with their game (NexusMods domain or mod.io game id).</param>
    /// <param name="track">True to track, false to untrack.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A one-line summary for the status bar.</returns>
    public async Task<string> ApplyAsync(
        IModBackend backend,
        IReadOnlyList<(string ModId, string? Game)> mods,
        bool track,
        CancellationToken ct = default)
    {
        if (mods.Count == 0)
            return "Nothing selected";

        var terms = ((ITrackingBackend)backend).Terms;
        var changed = 0;
        var unchanged = 0;
        var skipped = 0;
        var errors = new List<string>();

        foreach (var group in mods.GroupBy(m => m.Game))
        {
            var (installation, inGame) = await InGameForAsync(backend, group.Key, ct);

            if (inGame != null)
            {
                if (inGame.Manager.IsGameRunning())
                    return "Baldur's Gate 3 is running. Close it first: it writes its mods folder while it runs.";

                var question = track
                    ? $"Subscribe to {group.Count()} mod(s)?\n\nBaldur's Gate 3 downloads its mod.io subscriptions itself on its next launch. " +
                      "Modular records them and installs nothing, so there is only ever one copy."
                    : $"Unsubscribe from {group.Count()} mod(s)?\n\nThe game's copy of each and its load-order entry are removed now.";
                if (!await _dialogs.ShowConfirmationAsync(terms.Verb, question))
                    return "Cancelled";
            }

            var disable = new Dictionary<string, IReadOnlyList<ModularCopy>>();

            async Task<bool> BeforeAsync(string modId, CancellationToken token)
            {
                if (!track || inGame == null)
                    return true;

                var copies = (await inGame.FindModularCopiesAsync(installation!, modId, token)).Where(c => c.Enabled).ToList();
                if (copies.Count == 0)
                    return true;

                var names = string.Join(", ", copies.Select(c => c.Name));
                if (!await _dialogs.ShowConfirmationAsync("Already installed",
                        $"Modular already deploys mod {modId} as {names}. Subscribing would give the game a second copy.\n\n" +
                        "Disable Modular's copy and let the game's download be the only one? (No skips this mod.)"))
                {
                    return false;
                }

                disable[modId] = copies;
                return true;
            }

            async Task AfterAsync(TrackingItemResult item, CancellationToken token)
            {
                if (item.Result == null)
                {
                    if (item.Error == "skipped")
                        skipped++;
                    else
                        errors.Add($"{item.Input}: {item.Error}");
                    return;
                }

                if (item.Result.Changed) changed++; else unchanged++;
                if (inGame == null || item.ModId == null)
                    return;

                if (track)
                {
                    await inGame.RecordSubscriptionAsync(installation!, item.ModId, item.Result.Mod?.Name, null, token);
                    if (disable.TryGetValue(item.ModId, out var copies))
                    {
                        var result = await _install.Deployment.SetEnabledAsync(
                            installation!, copies.Select(c => c.ModId).ToList(), enabled: false, token);
                        if (!result.Success)
                            errors.Add($"could not disable Modular's copy of {item.ModId}: {result.Error}");
                    }
                }
                else
                {
                    await inGame.RemoveAsync(installation!, item.ModId, token);
                }
            }

            await _tracking.ApplyAsync(
                backend, group.Select(m => m.ModId).ToList(), group.Key, track, BeforeAsync, AfterAsync, ct);
        }

        if (errors.Count > 0)
            await _dialogs.ShowErrorAsync(terms.Verb, string.Join("\n", errors));

        var verb = track ? terms.ListName.ToLowerInvariant() : "un" + terms.ListName.ToLowerInvariant();
        return $"{changed} {verb}, {unchanged} unchanged" +
               (skipped > 0 ? $", {skipped} skipped" : "") +
               (errors.Count > 0 ? $", {errors.Count} failed" : "");
    }

    /// <summary>
    /// Records what Baldur's Gate 3's own mod manager has downloaded.
    /// </summary>
    /// <param name="subscribed">The mod.io subscriptions, by id, with names.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Each subscription's state in the game, by mod id, or empty when the game is not found.</returns>
    public async Task<IReadOnlyDictionary<string, string>> SyncBaldursGate3Async(
        IReadOnlyDictionary<string, string> subscribed, CancellationToken ct = default)
    {
        var installation = await _install.Deployment.ResolveInstallationAsync(
            InGameModManagers.BaldursGate3SteamAppId.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        var manager = installation == null ? null : InGameModManagers.For(installation);
        if (installation == null || manager == null)
            return new Dictionary<string, string>();

        var service = new InGameModService(new GameOwnedModRepository(_database), _install.Deployment.Mods, manager);
        var report = await service.SyncAsync(installation, subscribed.Keys.ToHashSet(StringComparer.Ordinal), subscribed, ct);

        var states = (await new GameOwnedModRepository(_database).ListAsync(installation.GameKey, ct))
            .ToDictionary(
                r => r.RemoteModId,
                r => r.State == GameOwnedModState.Installed ? "In game" : "Downloads on next launch");

        foreach (var item in report.Items)
        {
            states[item.RemoteModId] = item.Kind switch
            {
                InGameSyncKind.UpdatedByGame => "Updated by the game",
                InGameSyncKind.ModularCopy => "Second copy (Modular)",
                InGameSyncKind.Duplicate => "Duplicate files",
                _ => states.GetValueOrDefault(item.RemoteModId, "")
            };
        }

        return states;
    }

    private async Task<(GameInstallation? Installation, InGameModService? Service)> InGameForAsync(
        IModBackend backend, string? game, CancellationToken ct)
    {
        if (!string.Equals(backend.Id, BackendIds.ModIo, StringComparison.OrdinalIgnoreCase) ||
            game != BG3InGameModManager.ModIoGameId)
        {
            return (null, null);
        }

        var installation = await _install.Deployment.ResolveInstallationAsync(
            InGameModManagers.BaldursGate3SteamAppId.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        var manager = installation == null ? null : InGameModManagers.For(installation);
        return installation == null || manager == null
            ? (null, null)
            : (installation, new InGameModService(new GameOwnedModRepository(_database), _install.Deployment.Mods, manager));
    }
}
