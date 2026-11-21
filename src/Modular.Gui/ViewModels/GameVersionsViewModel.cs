using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Depots;
using Modular.Core.Security;
using Modular.Core.Steam;
using Modular.Gui.Services;

namespace Modular.Gui.ViewModels;

/// <summary>One version in a game's version list.</summary>
public sealed class GameVersionRow(GameVersion version)
{
    public GameVersion Version { get; } = version;
    public string Label => Version.Label;
    public string BuildText => Version.BuildId is { } b ? $"build {b}" : Version.Description ?? "";
    public string DateText => Version.Date is { } d ? d.LocalDateTime.ToString("yyyy-MM-dd") : "";
    public bool IsInstalled => Version.IsInstalled;
    public bool CanSwitch => !Version.IsInstalled && !Version.PasswordRequired;
    public string Note => Version.PasswordRequired ? "needs a branch password"
        : Version.Kind == GameVersionKind.ArchivedBuild ? "archived build" : Version.Description ?? "";
}

/// <summary>
/// A Steam game's version browser on its page: the installed build, its pin and lock, the
/// versions Steam serves (branches) and Modular has archived, and switching between them.
/// </summary>
public partial class GameVersionsViewModel : ViewModelBase
{
    private readonly uint _appId;
    private readonly string _gameName;
    private readonly IDialogService? _dialogs;
    private readonly DepotVersionService _service = new();

    [ObservableProperty] private string _installedText = "Reading the installed build…";
    [ObservableProperty] private string _pinText = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private bool _isLocked;
    [ObservableProperty] private bool _reportsCurrent;
    [ObservableProperty] private bool _steamRunning;
    [ObservableProperty] private bool _hasStagedSwitch;

    /// <summary>After switching to a non-branch build, tell Steam it's current so Play works without updating.</summary>
    [ObservableProperty] private bool _tellSteamCurrentAfterSwitch = true;

    /// <summary>After switching to an older build, lock its files so Steam can't overwrite them.</summary>
    [ObservableProperty] private bool _lockAfterSwitch = true;

    public ObservableCollection<GameVersionRow> Versions { get; } = new();
    public ObservableCollection<string> Drift { get; } = new();

    public bool HasDrift => Drift.Count > 0;
    public bool CanPin => !IsPinned;
    public bool CanUnpin => IsPinned;
    public bool CanLock => IsPinned && !IsLocked;
    public bool CanUnlock => IsLocked;

    // Designer constructor
    public GameVersionsViewModel() : this(107100, "Bastion", null, load: false)
    {
        InstalledText = "Installed: legacy · build 322538 · files from 2013-02-07";
        PinText = "Pinned · Steam told it's current · Locked";
    }

    public GameVersionsViewModel(uint appId, string gameName, IDialogService? dialogs, bool load = true)
    {
        _appId = appId;
        _gameName = gameName;
        _dialogs = dialogs;
        if (load)
            _ = RefreshAsync();
    }

    partial void OnIsPinnedChanged(bool value) => NotifyActions();
    partial void OnIsLockedChanged(bool value) => NotifyActions();

    private void NotifyActions()
    {
        OnPropertyChanged(nameof(CanPin));
        OnPropertyChanged(nameof(CanUnpin));
        OnPropertyChanged(nameof(CanLock));
        OnPropertyChanged(nameof(CanUnlock));
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var status = await Task.Run(() => _service.GetStatusAsync(_appId));
            SteamRunning = _service.SteamRunning;
            HasStagedSwitch = status.StagedSwapWaiting;
            var pin = status.Pin;
            IsPinned = pin is not null;
            IsLocked = pin is { Locked: true };
            ReportsCurrent = pin is { ReportsCurrent: true };
            PinText = pin is null ? "Not pinned: Steam updates it normally."
                : string.Join(" · ", new[]
                {
                    pin.Branch is { } b && b != "public" ? $"Pinned to branch {b}" : "Pinned (update only on launch)",
                    pin.ReportsCurrent ? "Steam told it's current" : null,
                    pin.Locked ? "Locked" : null,
                    status.Superseded ? "Steam replaced this build; unpin to clear" : null,
                }.Where(x => x is not null));
            Drift.Clear();
            foreach (var d in status.Drift)
                Drift.Add(d);
            OnPropertyChanged(nameof(HasDrift));

            IReadOnlyList<GameVersion> versions;
            try
            {
                await using var session = await SteamSession.OpenStoredAsync(new SteamAccountStore(new DefaultSecretStore()), default);
                versions = await _service.ListVersionsAsync(status, new SteamDepotClient(session));
            }
            catch (Exception ex) when (ex is SteamNotLoggedInException or SteamLogOnException or SteamDisconnectedException or TimeoutException)
            {
                versions = await _service.ListVersionsAsync(status, null);
                Message = "Steam versions need a one-time login: run 'modular steam login' in a terminal. Archived builds are listed meanwhile.";
            }
            Versions.Clear();
            foreach (var v in versions)
                Versions.Add(new GameVersionRow(v));

            var installed = versions.FirstOrDefault(v => v.IsInstalled);
            var oldest = status.Depots.Where(d => d.BuildDate is not null).Select(d => d.BuildDate!.Value).DefaultIfEmpty().Max();
            InstalledText = installed is not null
                ? $"Installed: {installed.Label}{(installed.BuildId is { } build ? $" · build {build}" : "")}{(oldest != default ? $" · files from {oldest:yyyy-MM-dd}" : "")}"
                : status.Depots.Any(d => d.Match is DepotMatch.Unknown or DepotMatch.NoCandidates or DepotMatch.Modified)
                    ? "Installed build: can't tell (modified or unknown files)."
                    : $"Installed: a build from {oldest:yyyy-MM-dd} that no branch serves.";
        }
        catch (DepotOperationException ex)
        {
            InstalledText = ex.Message;
        }
        catch (Exception ex)
        {
            Message = $"Couldn't read versions: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunAsync(string doing, Func<Task<string>> action)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        Message = doing;
        try
        {
            Message = await action();
        }
        catch (Exception ex) when (ex is DepotOperationException or SteamNotLoggedInException or SteamLogOnException
                                       or SteamDisconnectedException or InvalidOperationException or IOException or DepotAccessException)
        {
            Message = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync();
    }

    private static async Task<T> WithClientAsync<T>(Func<SteamDepotClient, SteamSession, Task<T>> work)
    {
        await using var session = await SteamSession.OpenStoredAsync(new SteamAccountStore(new DefaultSecretStore()), default);
        return await work(new SteamDepotClient(session), session);
    }

    [RelayCommand]
    private Task PinAsync() => RunAsync("Pinning…", async () =>
    {
        await _service.PinAsync(_appId, null, asCurrent: false, client: null);
        return "Pinned: Steam updates it only when you launch it.";
    });

    [RelayCommand]
    private Task PinAsCurrentAsync() => RunAsync("Pinning and telling Steam it's current…", () => WithClientAsync(async (client, _) =>
    {
        await _service.PinAsync(_appId, null, asCurrent: true, client);
        return "Pinned. Steam sees it as up to date, so Play launches this build. Lock it too, so a 'verify files' can't overwrite it.";
    }));

    [RelayCommand]
    private async Task UnpinAsync()
    {
        if (_dialogs is not null && !await _dialogs.ShowConfirmationAsync("Unpin",
                $"Unpin {_gameName}? Steam's own update settings come back, and Steam will update the game to its latest build."))
            return;
        await RunAsync("Unpinning…", async () =>
        {
            var kept = await _service.UnpinAsync(_appId);
            return "Unpinned. Steam will update the game normally." + (kept.Count > 0 ? $" Left as Steam set them: {string.Join(", ", kept)}." : "");
        });
    }

    [RelayCommand]
    private Task LockAsync() => RunAsync("Locking the build's files…", async () =>
    {
        var r = await _service.SetLockAsync(_appId, locked: true);
        return $"Locked {r.Changed + r.Unchanged} file(s). Steam can't change them; it shows a disk-write error if it tries.";
    });

    [RelayCommand]
    private Task UnlockAsync() => RunAsync("Unlocking…", async () =>
    {
        var r = await _service.SetLockAsync(_appId, locked: false);
        return $"Unlocked {r.Changed + r.Unchanged} file(s). Still pinned.";
    });

    [RelayCommand]
    private async Task SwitchToAsync(GameVersionRow? row)
    {
        if (row is null || !row.CanSwitch || IsBusy)
            return;
        IsBusy = true;
        StagePlanResult plan;
        try
        {
            Message = $"Working out what {row.Label} needs…";
            plan = await WithClientAsync((client, _) => _service.PlanStageAsync(_appId, row.Version.Target, client));
        }
        catch (Exception ex) when (ex is DepotOperationException or SteamNotLoggedInException or SteamLogOnException
                                       or SteamDisconnectedException or DepotAccessException or InvalidOperationException)
        {
            Message = ex.Message;
            IsBusy = false;
            return;
        }
        IsBusy = false;
        if (plan.Items.Count == 0)
        {
            Message = "Already installed.";
            return;
        }

        var downgrade = plan.Items.Any(i => i.To.CreationTime < i.From.CreationTime);
        var text = $"Switch {_gameName} to {row.Label}{(row.Version.BuildId is { } b ? $" (build {b})" : "")}?\n\n" +
            $"Download: {plan.DownloadBytes / 1e6:F0} MB; {plan.Items.Sum(i => i.Plan.Files.Count)} file(s) change.\n" +
            (downgrade ? "This is a downgrade: saves made on the newer build may not load on the older one.\n" : "") +
            "Steam must be closed for the last step; Modular downloads first and asks you to close it then.";
        if (_dialogs is not null && !await _dialogs.ShowConfirmationAsync("Switch version", text))
            return;

        await RunAsync("Downloading…", () => WithClientAsync(async (_, session) =>
        {
            await _service.StageAsync(plan, session, new Progress<(uint DepotId, long Done, long Total)>(p =>
                Message = $"Downloading depot {p.DepotId}: {100.0 * p.Done / Math.Max(1, p.Total):F0}%"));
            if (_service.SteamRunning)
                return $"{row.Label} is downloaded and verified. Close Steam (Steam → Exit), then press Finish switch.";
            return await FinishAsync(plan.Branch is null);
        }));
    }

    [RelayCommand]
    private Task FinishSwitchAsync() => RunAsync("Switching…", async () =>
    {
        var staged = StagedSwap.Load(SteamAppManifest.Find(_appId)!);
        return await FinishAsync(staged?.Branch is null);
    });

    private async Task<string> FinishAsync(bool nonBranch)
    {
        var swap = await _service.PrepareSwapAsync(_appId);
        var pin = await _service.ApplySwapAsync(swap, new Progress<string>(m => Message = m));
        var parts = new List<string> { "Switched and pinned." };
        if (nonBranch && TellSteamCurrentAfterSwitch)
        {
            await WithClientAsync(async (client, _) => await _service.PinAsync(_appId, null, asCurrent: true, client));
            parts.Add("Steam is told it's current, so Play launches this build.");
        }
        var downgraded = swap.IsDowngrade;
        if (downgraded && LockAfterSwitch && !pin.Locked)
        {
            await _service.SetLockAsync(_appId, locked: true);
            parts.Add("Files locked.");
        }
        return string.Join(" ", parts);
    }

    [RelayCommand]
    private Task DiscardStagedAsync() => RunAsync("Removing the downloaded build…", () =>
    {
        _service.DiscardStaged(_appId);
        return Task.FromResult("Discarded the downloaded build.");
    });
}
