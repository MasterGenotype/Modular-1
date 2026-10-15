using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Configuration;
using Modular.Gui.Services;
using Modular.Switch.Totk;
using TkSharp.Core.Models;

namespace Modular.Gui.ViewModels;

/// <summary>
/// Tears of the Kingdom in the Switch panel: the game dump and keys, the current
/// profile's mods in priority order with enable toggles, and merging and deploying.
/// </summary>
/// <remarks>
/// TkSharp is not safe to call concurrently, so every operation runs behind
/// <see cref="IsBusy"/>, one at a time, off the UI thread.
/// </remarks>
public partial class TotkViewModel : ViewModelBase
{
    private readonly Lazy<TotkMergeService>? _service;
    private readonly AppSettings? _appSettings;
    private readonly ConfigurationService? _configService;
    private readonly IDialogService? _dialogService;
    private Task? _load;

    public IReadOnlyList<string> GameVersions { get; } = TotkSettings.GameVersions;

    public IReadOnlyList<string> GameLanguages { get; } =
        TotkSettings.GameLanguages.Select(l => l.Value).ToList();

    // ── Settings (edited here, saved with SaveSettings) ───────────────────

    [ObservableProperty] private string _keysFolderPath = string.Empty;
    [ObservableProperty] private string _baseGamePaths = string.Empty;
    [ObservableProperty] private string _updatePaths = string.Empty;
    [ObservableProperty] private string _nandFolderPaths = string.Empty;
    [ObservableProperty] private string _gameDumpFolderPaths = string.Empty;
    [ObservableProperty] private string _sdCardRootPath = string.Empty;
    [ObservableProperty] private string _emulatorPath = string.Empty;
    [ObservableProperty] private string _preferredGameVersion = TotkSettings.AutoGameVersion;
    [ObservableProperty] private string _gameLanguage = "USen";
    [ObservableProperty] private string _mergeOutput = string.Empty;
    [ObservableProperty] private bool _useRomfslite;

    // ── State ─────────────────────────────────────────────────────────────

    [ObservableProperty] private ObservableCollection<TotkModItem> _mods = new();
    [ObservableProperty] private ObservableCollection<string> _profiles = new();
    [ObservableProperty] private string? _currentProfile;
    [ObservableProperty] private string _mergeOutputFolder = string.Empty;
    [ObservableProperty] private string _gameStatus = "Not checked";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MergeCommand), nameof(InstallModCommand), nameof(InstallFolderCommand),
        nameof(DeployCommand), nameof(CheckGameCommand), nameof(SaveSettingsCommand), nameof(CreateProfileCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private string _statusMessage = "Configure the game dump and keys, then install mods";

    // Designer constructor
    public TotkViewModel()
    {
        Profiles.Add("Default");
        CurrentProfile = "Default";
        Mods.Add(new TotkModItem(null) { Position = 1, Name = "Playable Riju", Version = "1.0.4", IsEnabled = true });
        Mods.Add(new TotkModItem(null) { Position = 2, Name = "60 FPS", Version = "1.2.0", IsEnabled = false });
    }

    // DI constructor
    public TotkViewModel(
        Lazy<TotkMergeService> service,
        AppSettings appSettings,
        ConfigurationService configService,
        IDialogService dialogService)
    {
        _service = service;
        _appSettings = appSettings;
        _configService = configService;
        _dialogService = dialogService;
        LoadSettingsFields();
    }

    /// <summary>Opens the mod store the first time TotK is shown.</summary>
    public Task EnsureLoadedAsync() => _load ??= LoadAsync();

    private async Task LoadAsync()
    {
        if (_service == null)
            return;

        await RunAsync("Opening the TotK mod store", () => _ = _service.Value);
        Refresh();
        StatusMessage = Mods.Count == 0
            ? "Configure the game dump and keys, then install mods"
            : $"{Mods.Count} mod(s) in {CurrentProfile}";
    }

    // ── Commands ──────────────────────────────────────────────────────────

    private bool CanRun() => !IsBusy && _service != null;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task SaveSettingsAsync()
    {
        ApplySettingsFields();
        await _configService!.SaveAsync(_appSettings!);
        MergeOutputFolder = _service!.Value.MergedOutputFolder;
        StatusMessage = "Settings saved";
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task CheckGameAsync()
    {
        ApplySettingsFields();
        string? error = null;
        int? version = null;
        await RunAsync("Reading the game", () =>
        {
            using var rom = _service!.Value.TryGetTkRom(out _, out _, out error);
            version = rom?.GameVersion;
        });
        GameStatus = version is { } v ? $"Game found (version {v})" : $"Game not found: {error}";
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task InstallModAsync()
    {
        var files = await _dialogService!.ShowFileBrowserAsync("Select TotK mods (.zip, .rar, .7z, .tkcl)", allowMultiple: true);
        await InstallAsync(files);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task InstallFolderAsync()
    {
        var folder = await _dialogService!.ShowFolderBrowserAsync("Select a TotK mod folder (containing romfs, exefs or cheats)");
        if (!string.IsNullOrEmpty(folder))
            await InstallAsync([folder]);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task MergeAsync()
    {
        ApplySettingsFields();
        var service = _service!.Value;
        var progress = new Progress<TotkProgress>(p => ProgressText = p.Detail is null ? p.Phase : $"{p.Phase}: {p.Detail}");
        if (await RunAsync("Merging", () => service.MergeAsync(progress: progress).AsTask()))
            StatusMessage = $"Merged {CurrentProfile} into {service.MergedOutputFolder}";
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task DeployAsync()
    {
        ApplySettingsFields();
        TotkDeployResult? result = null;
        await RunAsync("Deploying", () => result = _service!.Value.Deploy());
        if (result is null)
            return;

        StatusMessage = result.Outcome switch
        {
            TotkDeployOutcome.Linked => $"Linked {result.Target}",
            TotkDeployOutcome.AlreadyLinked => $"Already linked: {result.Target}",
            TotkDeployOutcome.Copied => $"Symlinks unavailable; copied to {result.Target} (deploy again after each merge)",
            TotkDeployOutcome.Skipped => result.Message ?? "Nothing to do",
            _ => $"Not deployed: {result.Message}"
        };
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task CreateProfileAsync()
    {
        var name = await _dialogService!.ShowInputAsync("New TotK profile", "Profile name");
        if (string.IsNullOrWhiteSpace(name))
            return;

        if (await RunAsync("Creating profile", () => _service!.Value.CreateProfile(name.Trim())))
            Refresh();
    }

    [RelayCommand]
    private async Task UninstallAsync(TotkModItem? item)
    {
        if (item?.ProfileMod is not { } profileMod || IsBusy)
            return;

        var confirmed = await _dialogService!.ShowConfirmationAsync("Uninstall TotK mod",
            $"Uninstall '{item.Name}' from every profile and merge again?");
        if (!confirmed)
            return;

        if (await RunAsync($"Uninstalling {item.Name}", () => _service!.Value.UninstallAsync(profileMod.Mod).AsTask()))
            StatusMessage = $"Uninstalled {item.Name} and merged again";
        Refresh();
    }

    [RelayCommand]
    private void MoveUp(TotkModItem? item) => Move(item, -1);

    [RelayCommand]
    private void MoveDown(TotkModItem? item) => Move(item, +1);

    partial void OnCurrentProfileChanged(string? value)
    {
        if (_service is not { IsValueCreated: true } || value is null)
            return;

        var service = _service.Value;
        if (service.FindProfile(value) is not { } profile || profile == service.CurrentProfile)
            return;

        _ = SelectProfileAsync(profile);
    }

    // ── Internals ─────────────────────────────────────────────────────────

    private async Task SelectProfileAsync(TkProfile profile)
    {
        // Selecting a profile runs TKMM's basic merge (cheats, exefs).
        if (await RunAsync($"Selecting {profile.Name}", () => _service!.Value.SelectProfile(profile)))
            StatusMessage = $"Selected {profile.Name}; merge to apply its romfs";
        Refresh();
    }

    private async Task InstallAsync(IReadOnlyList<string> inputs)
    {
        if (inputs.Count == 0)
            return;

        ApplySettingsFields();
        var service = _service!.Value;
        var installed = new List<string>();
        var failed = new List<string>();
        var progress = new Progress<TotkProgress>(p => ProgressText = p.Detail is null ? p.Phase : $"{p.Phase}: {p.Detail}");

        await RunAsync("Installing", async () =>
        {
            foreach (var input in inputs)
            {
                var mod = await service.InstallAsync(input, progress: progress);
                (mod is null ? failed : installed).Add(mod?.Name ?? Path.GetFileName(input));
            }
        });

        // Installing may fill in the 7-Zip path.
        await _configService!.SaveAsync(_appSettings!);
        Refresh();
        StatusMessage = failed.Count == 0
            ? $"Installed {string.Join(", ", installed)}; merge to apply"
            : $"Installed {installed.Count}, failed: {string.Join(", ", failed)}";
    }

    private void Move(TotkModItem? item, int direction)
    {
        if (item?.ProfileMod is not { } profileMod || IsBusy || _service is null)
            return;

        var profile = _service.Value.CurrentProfile;
        TotkMergeService.Move(profile, profileMod, profile.Mods.IndexOf(profileMod) + direction);
        Refresh();
    }

    /// <summary>Runs <paramref name="action"/> off the UI thread behind <see cref="IsBusy"/>.</summary>
    /// <returns>False when it failed (the error is in <see cref="StatusMessage"/>).</returns>
    private async Task<bool> RunAsync(string what, Func<Task> action)
    {
        IsBusy = true;
        ProgressText = what;
        StatusMessage = what + "…";
        try
        {
            await Task.Run(action);
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"{what} failed: {ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
            ProgressText = string.Empty;
        }
    }

    private Task<bool> RunAsync(string what, Action action) => RunAsync(what, () =>
    {
        action();
        return Task.CompletedTask;
    });

    private void Refresh()
    {
        if (_service is not { IsValueCreated: true })
            return;

        var service = _service.Value;
        var profile = service.CurrentProfile;

        Mods.Clear();
        for (var i = 0; i < profile.Mods.Count; i++)
        {
            var pm = profile.Mods[i];
            Mods.Add(new TotkModItem(pm)
            {
                Position = i + 1,
                Name = pm.Mod.Name,
                Version = pm.Mod.Version,
                IsEnabled = pm.IsEnabled
            });
        }

        Profiles.Clear();
        foreach (var p in service.ModManager.Profiles)
            Profiles.Add(p.Name);
        CurrentProfile = profile.Name;
        MergeOutputFolder = service.MergedOutputFolder;
    }

    private void LoadSettingsFields()
    {
        var s = _appSettings!.SwitchTotk;
        KeysFolderPath = s.KeysFolderPath ?? string.Empty;
        BaseGamePaths = string.Join(Environment.NewLine, s.PackagedBaseGamePaths);
        UpdatePaths = string.Join(Environment.NewLine, s.PackagedUpdatePaths);
        NandFolderPaths = string.Join(Environment.NewLine, s.NandFolderPaths);
        GameDumpFolderPaths = string.Join(Environment.NewLine, s.GameDumpFolderPaths);
        SdCardRootPath = s.SdCardRootPath ?? string.Empty;
        EmulatorPath = s.EmulatorPath ?? string.Empty;
        PreferredGameVersion = s.PreferredGameVersion;
        GameLanguage = s.GameLanguage;
        MergeOutput = s.MergeOutput ?? string.Empty;
        UseRomfslite = s.UseRomfslite;
    }

    private void ApplySettingsFields()
    {
        if (_appSettings is null)
            return;

        var s = _appSettings.SwitchTotk;
        s.KeysFolderPath = NullIfBlank(KeysFolderPath);
        s.PackagedBaseGamePaths = Lines(BaseGamePaths);
        s.PackagedUpdatePaths = Lines(UpdatePaths);
        s.NandFolderPaths = Lines(NandFolderPaths);
        s.GameDumpFolderPaths = Lines(GameDumpFolderPaths);
        s.SdCardRootPath = NullIfBlank(SdCardRootPath);
        s.EmulatorPath = NullIfBlank(EmulatorPath);
        s.PreferredGameVersion = PreferredGameVersion;
        s.GameLanguage = GameLanguage;
        s.UseRomfslite = UseRomfslite;

        // A new merge output is only created; neither folder is moved or deleted.
        if (_service is { IsValueCreated: true } && NullIfBlank(MergeOutput) != s.MergeOutput)
            _service.Value.SetMergeOutput(NullIfBlank(MergeOutput));
        else
            s.MergeOutput = NullIfBlank(MergeOutput);
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<string> Lines(string value) =>
        value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

/// <summary>A mod in the current TotK profile, as the list shows it.</summary>
public partial class TotkModItem(TkProfileMod? profileMod) : ObservableObject
{
    public TkProfileMod? ProfileMod { get; } = profileMod;

    [ObservableProperty] private int _position;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _version = string.Empty;
    [ObservableProperty] private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        // Saved by TkSharp as soon as it changes.
        if (ProfileMod != null && ProfileMod.IsEnabled != value)
            ProfileMod.IsEnabled = value;
    }
}
