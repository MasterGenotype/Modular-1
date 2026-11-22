using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Switch.Models;
using Modular.Switch.Scanner;
using Modular.Switch.Totk;
using Modular.Gui.Services;

namespace Modular.Gui.ViewModels;

/// <summary>An emulator tile on the Games tab.</summary>
public sealed class EmulatorDisplayModel(SwitchEmulator emulator)
{
    public SwitchEmulator Emulator { get; } = emulator;
    public string Name => Emulator.Name;
    public string Caption => Emulator.Family == SwitchEmulatorFamily.Yuzu ? "Switch · yuzu family" : "Switch · Ryujinx";
}

/// <summary>A game tile on an emulator's page.</summary>
public sealed class SwitchTitleDisplayModel(SwitchTitleInfo title)
{
    public SwitchTitleInfo Title { get; } = title;
    public string Name => Title.Name;
    public string Caption => Title.Version is { } v ? $"v{v}" : Title.TitleId;
    public string ModsText => Title.ModFolderEntries switch
    {
        0 => "no mods",
        1 => "1 item in its mod folder",
        var n => $"{n} items in its mod folder",
    };
}

/// <summary>An emulator's own page: where it lives and the games it knows.</summary>
public partial class EmulatorPageViewModel : ViewModelBase
{
    private readonly GameDetectionViewModel? _owner;
    private readonly IDialogService? _dialogs;
    private readonly Dictionary<string, SwitchTitlePageViewModel> _titlePages = new(StringComparer.OrdinalIgnoreCase);

    // Designer constructor
    public EmulatorPageViewModel() : this(new SwitchEmulator("yuzu", SwitchEmulatorFamily.Yuzu, "/home/me/.local/share/yuzu", null, null, "/usr/bin/yuzu"), null, null)
    {
    }

    public EmulatorPageViewModel(SwitchEmulator emulator, GameDetectionViewModel? owner, IDialogService? dialogs)
    {
        Emulator = emulator;
        _owner = owner;
        _dialogs = dialogs;
        Refresh();
    }

    public SwitchEmulator Emulator { get; }
    public ObservableCollection<SwitchTitleDisplayModel> Titles { get; } = new();
    public string ExecutableText => Emulator.Executable ?? "program not found (data folder only)";
    public string GameDirsText => Emulator.GameDirs() is { Count: > 0 } dirs ? string.Join(", ", dirs) : "only the emulator's built-in locations";

    [ObservableProperty]
    private string _summary = "";

    [RelayCommand]
    private void Refresh()
    {
        Titles.Clear();
        foreach (var t in SwitchEmulatorDetector.Titles(Emulator))
            Titles.Add(new SwitchTitleDisplayModel(t));
        Summary = Titles.Count == 1 ? "1 game" : $"{Titles.Count} games";
    }

    [RelayCommand]
    private void OpenTitle(SwitchTitleDisplayModel? title)
    {
        if (title is null || _owner is null)
            return;
        if (!_titlePages.TryGetValue(title.Title.TitleId, out var page))
            _titlePages[title.Title.TitleId] = page = new SwitchTitlePageViewModel(Emulator, title.Title, this, _owner, _dialogs);
        else
            page.Reload();
        _owner.ActivePage = page;
    }

    [RelayCommand]
    private void OpenDataFolder() => FolderOpener.Open(Emulator.DataRoot);

    [RelayCommand]
    private async Task BackAsync()
    {
        if (_owner is not null)
            await _owner.CloseActivePageAsync(rescan: false);
    }
}

/// <summary>One installed mod on a Switch game's page.</summary>
public sealed record SwitchModRow(string Name, string State, string Source);

/// <summary>A Switch game's page: version, mod folder and the mods it has.</summary>
public partial class SwitchTitlePageViewModel : ViewModelBase
{
    private readonly SwitchEmulator _emulator;
    private readonly EmulatorPageViewModel? _back;
    private readonly GameDetectionViewModel? _owner;

    // Designer constructor
    public SwitchTitlePageViewModel() : this(
        new SwitchEmulator("yuzu", SwitchEmulatorFamily.Yuzu, "/home/me/.local/share/yuzu", null, null, null),
        new SwitchTitleInfo(TotkTitle.TitleIdValue, "The Legend of Zelda: Tears of the Kingdom", "1.2.1", "SDMC (LayeredExeFS, LayeredFS)", "/load/0100F2C0115B6000", 1),
        null, null, null)
    {
    }

    public SwitchTitlePageViewModel(SwitchEmulator emulator, SwitchTitleInfo title, EmulatorPageViewModel? back,
        GameDetectionViewModel? owner, IDialogService? dialogs)
    {
        _emulator = emulator;
        Title = title;
        _back = back;
        _owner = owner;
        Reload();
    }

    public SwitchTitleInfo Title { get; }
    public string EmulatorName => _emulator.Name;
    public string VersionText => Title.Version ?? "unknown (not in the emulator's game list yet)";
    public string AddOnsText => string.IsNullOrEmpty(Title.AddOns) ? "none" : Title.AddOns;
    public bool IsTotk => string.Equals(Title.TitleId, TotkTitle.TitleIdValue, StringComparison.OrdinalIgnoreCase);
    public string HowModsWork => IsTotk
        ? "Tears of the Kingdom mods are merged by Modular's TotK merger (a TKMM port) into one output in the mod folder. Manage them under Mod Manager → Tears of the Kingdom."
        : "Each mod is its own LayeredFS folder in the emulator's mod folder. Install them under Mod Manager → Switch games.";

    public ObservableCollection<SwitchModRow> Mods { get; } = new();

    [ObservableProperty]
    private string _modsSummary = "";

    public bool HasMods => Mods.Count > 0;

    internal void Reload() => _ = LoadModsAsync();

    private async Task LoadModsAsync()
    {
        Mods.Clear();
        try
        {
            if (IsTotk && _owner?.Totk is { } totk)
            {
                // The merge profile is the list of TotK mods; reading it uses the one shared merge service.
                var profile = totk.Value.CurrentProfile;
                foreach (var m in profile.Mods)
                    Mods.Add(new SwitchModRow(m.Mod.Name, m.IsEnabled ? "Enabled" : "Disabled", $"merge profile \"{profile.Name}\""));
            }
            else
            {
                var state = await SwitchInstallState.LoadAsync();
                var tracked = SwitchTitleId.TryParse(Title.TitleId, out var id) ? state.ForTitle(id).ToList() : [];
                foreach (var m in tracked.OrderBy(m => m.LoadOrder))
                    Mods.Add(new SwitchModRow(m.Name, m.IsInstalled ? "Installed" : "Not deployed", "installed by Modular"));
                if (Directory.Exists(Title.ModDir))
                {
                    var known = tracked.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var folder in Directory.EnumerateDirectories(Title.ModDir).Select(Path.GetFileName).OfType<string>()
                                 .Where(f => !known.Contains(f)).OrderBy(f => f))
                        Mods.Add(new SwitchModRow(folder, "In the mod folder", "not installed by Modular"));
                }
            }
            ModsSummary = Mods.Count == 0 ? "No mods for this game." : $"{Mods.Count} mod(s).";
        }
        catch (Exception ex)
        {
            ModsSummary = $"Couldn't read the mods: {ex.Message}";
        }
        OnPropertyChanged(nameof(HasMods));
    }

    [RelayCommand]
    private void OpenModFolder() => FolderOpener.Open(Directory.Exists(Title.ModDir) ? Title.ModDir : _emulator.DataRoot);

    [RelayCommand]
    private void Back()
    {
        if (_owner is not null && _back is not null)
            _owner.ActivePage = _back;
    }
}
