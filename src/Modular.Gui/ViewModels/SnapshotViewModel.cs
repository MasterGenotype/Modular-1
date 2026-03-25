using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.GameDetection;
using Modular.Core.Installers;
using Modular.Core.Snapshots;
using Modular.Gui.Services;

namespace Modular.Gui.ViewModels;

public partial class SnapshotViewModel : ViewModelBase
{
    private readonly SnapshotManager? _snapshotManager;
    private readonly ModInstallationService? _installService;
    private readonly SteamGameScanner? _gameScanner;
    private readonly IDialogService? _dialogService;

    [ObservableProperty]
    private ObservableCollection<GameDisplayModel> _games = new();

    [ObservableProperty]
    private GameDisplayModel? _selectedGame;

    [ObservableProperty]
    private int _displayYear;

    [ObservableProperty]
    private int _displayMonth;

    [ObservableProperty]
    private string _displayMonthName = string.Empty;

    [ObservableProperty]
    private ObservableCollection<CalendarDayModel> _calendarDays = new();

    [ObservableProperty]
    private CalendarDayModel? _selectedDay;

    [ObservableProperty]
    private ObservableCollection<SnapshotDisplayModel> _daySnapshots = new();

    [ObservableProperty]
    private SnapshotDisplayModel? _selectedSnapshot;

    [ObservableProperty]
    private ObservableCollection<SnapshotEntryDisplayModel> _snapshotEntries = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "Select a game to view snapshots";

    // Designer constructor
    public SnapshotViewModel()
    {
        var now = DateTime.UtcNow;
        DisplayYear = now.Year;
        DisplayMonth = now.Month;
        UpdateDisplayMonthName();
        BuildCalendarGrid();

        Games.Add(new GameDisplayModel
        {
            AppId = 489830,
            DisplayName = "Skyrim Special Edition",
            InstallPath = "/home/user/.steam/steamapps/common/Skyrim"
        });
    }

    // DI constructor
    public SnapshotViewModel(
        SnapshotManager snapshotManager,
        ModInstallationService installService,
        SteamGameScanner gameScanner,
        IDialogService dialogService)
    {
        _snapshotManager = snapshotManager;
        _installService = installService;
        _gameScanner = gameScanner;
        _dialogService = dialogService;

        var now = DateTime.UtcNow;
        DisplayYear = now.Year;
        DisplayMonth = now.Month;
        UpdateDisplayMonthName();
        BuildCalendarGrid();

        _ = LoadGamesAsync();
    }

    partial void OnSelectedGameChanged(GameDisplayModel? value)
    {
        if (value != null)
        {
            var now = DateTime.UtcNow;
            DisplayYear = now.Year;
            DisplayMonth = now.Month;
            UpdateDisplayMonthName();
            _ = RefreshCalendarAsync();
        }
    }

    [RelayCommand]
    private void SelectDay(CalendarDayModel? day)
    {
        SelectedDay = day;
    }

    partial void OnSelectedDayChanged(CalendarDayModel? value)
    {
        if (value != null && value.IsCurrentMonth)
        {
            _ = LoadDaySnapshotsAsync(value.Date);
        }
        else
        {
            DaySnapshots.Clear();
            SelectedSnapshot = null;
        }
    }

    partial void OnSelectedSnapshotChanged(SnapshotDisplayModel? value)
    {
        if (value != null)
        {
            _ = LoadSnapshotEntriesAsync(value.SnapshotId);
        }
        else
        {
            SnapshotEntries.Clear();
        }
    }

    [RelayCommand]
    private async Task LoadGamesAsync()
    {
        if (_gameScanner == null) return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var results = await _gameScanner.ScanAllAsync(cts.Token);

            Games.Clear();
            foreach (var game in results.OrderBy(g => g.DisplayName))
            {
                Games.Add(new GameDisplayModel
                {
                    AppId = game.AppId,
                    DisplayName = game.DisplayName,
                    InstallPath = game.InstallPath,
                    IsFullyInstalled = game.IsFullyInstalled
                });
            }

            StatusMessage = $"Found {Games.Count} game(s)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error scanning games: {ex.Message}";
        }
    }

    [RelayCommand]
    private void PreviousMonth()
    {
        if (DisplayMonth == 1)
        {
            DisplayMonth = 12;
            DisplayYear--;
        }
        else
        {
            DisplayMonth--;
        }

        UpdateDisplayMonthName();
        _ = RefreshCalendarAsync();
    }

    [RelayCommand]
    private void NextMonth()
    {
        if (DisplayMonth == 12)
        {
            DisplayMonth = 1;
            DisplayYear++;
        }
        else
        {
            DisplayMonth++;
        }

        UpdateDisplayMonthName();
        _ = RefreshCalendarAsync();
    }

    [RelayCommand]
    private async Task CreateSnapshotAsync()
    {
        if (_snapshotManager == null || _dialogService == null || SelectedGame == null) return;

        var name = await _dialogService.ShowInputAsync(
            "Create Snapshot",
            "Enter a name for this snapshot (optional):",
            $"Snapshot {DateTime.Now:yyyy-MM-dd HH:mm}");

        if (name == null) return; // Cancelled

        IsLoading = true;
        StatusMessage = "Creating snapshot...";

        try
        {
            var snapshot = await _snapshotManager.CreateSnapshotAsync(
                SelectedGame.AppId,
                SelectedGame.DisplayName,
                SelectedGame.InstallPath,
                SnapshotTrigger.Manual,
                name: name);

            StatusMessage = $"Snapshot created: {snapshot.ModCount} mod(s) captured";
            await RefreshCalendarAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error creating snapshot: {ex.Message}";
            await _dialogService.ShowErrorAsync("Snapshot Error", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RestoreSnapshotAsync(SnapshotDisplayModel? snapshot)
    {
        if (_snapshotManager == null || _installService == null || _dialogService == null || snapshot == null)
            return;

        var confirmed = await _dialogService.ShowConfirmationAsync(
            "Restore Snapshot",
            $"Restore snapshot \"{snapshot.Name}\" ({snapshot.ModCount} mods)?\n\n" +
            "This will remove mods installed after this snapshot and reinstall any missing mods.");

        if (!confirmed) return;

        IsLoading = true;
        StatusMessage = "Restoring snapshot...";

        try
        {
            var result = await _snapshotManager.RestoreSnapshotAsync(
                snapshot.SnapshotId, _installService);

            if (result.Success)
            {
                StatusMessage = $"Restored: {result.ModsRemoved} removed, {result.ModsReinstalled} reinstalled";
            }
            else
            {
                var errorSummary = result.Errors.Count > 0
                    ? string.Join("\n", result.Errors.Take(5))
                    : result.Error ?? "Unknown error";
                StatusMessage = $"Partial restore: {result.ModsRemoved} removed, {result.ModsReinstalled} reinstalled, {result.Errors.Count} error(s)";
                await _dialogService.ShowErrorAsync("Restore Issues", errorSummary);
            }

            await RefreshCalendarAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Restore failed: {ex.Message}";
            await _dialogService.ShowErrorAsync("Restore Error", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task DeleteSnapshotAsync(SnapshotDisplayModel? snapshot)
    {
        if (_snapshotManager == null || _dialogService == null || snapshot == null) return;

        var confirmed = await _dialogService.ShowConfirmationAsync(
            "Delete Snapshot",
            $"Delete snapshot \"{snapshot.Name}\"?");

        if (!confirmed) return;

        try
        {
            await _snapshotManager.DeleteSnapshotAsync(snapshot.SnapshotId);
            DaySnapshots.Remove(snapshot);
            if (SelectedSnapshot == snapshot)
            {
                SelectedSnapshot = null;
                SnapshotEntries.Clear();
            }

            StatusMessage = "Snapshot deleted";
            await RefreshCalendarAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    private async Task RefreshCalendarAsync()
    {
        BuildCalendarGrid();

        if (_snapshotManager == null || SelectedGame == null) return;

        try
        {
            var snapshotDays = await _snapshotManager.GetSnapshotDatesAsync(
                SelectedGame.AppId, DisplayYear, DisplayMonth);

            foreach (var day in CalendarDays)
            {
                if (day.IsCurrentMonth && snapshotDays.Contains(day.DayNumber))
                {
                    day.HasSnapshots = true;
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading calendar: {ex.Message}";
        }
    }

    private async Task LoadDaySnapshotsAsync(DateTime date)
    {
        if (_snapshotManager == null || SelectedGame == null) return;

        DaySnapshots.Clear();
        SelectedSnapshot = null;
        SnapshotEntries.Clear();

        try
        {
            var startUtc = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Utc);
            var endUtc = startUtc.AddDays(1);
            var snapshots = await _snapshotManager.ListSnapshotsByDateRangeAsync(
                SelectedGame.AppId, startUtc, endUtc);

            foreach (var s in snapshots)
            {
                DaySnapshots.Add(new SnapshotDisplayModel
                {
                    SnapshotId = s.SnapshotId,
                    Name = s.Name ?? $"Snapshot {s.SnapshotId}",
                    CreatedAt = FormatTimestamp(s.CreatedAtUtc),
                    ModCount = s.ModCount,
                    Trigger = s.Trigger switch
                    {
                        SnapshotTrigger.AutoInstall => "Auto (Install)",
                        SnapshotTrigger.AutoUninstall => "Auto (Uninstall)",
                        _ => "Manual"
                    },
                    GameName = s.GameName
                });
            }

            StatusMessage = snapshots.Count > 0
                ? $"{snapshots.Count} snapshot(s) on {date:MMM d}"
                : $"No snapshots on {date:MMM d}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading snapshots: {ex.Message}";
        }
    }

    private async Task LoadSnapshotEntriesAsync(string snapshotId)
    {
        if (_snapshotManager == null) return;

        SnapshotEntries.Clear();

        try
        {
            var entries = await _snapshotManager.GetSnapshotEntriesAsync(snapshotId);

            foreach (var e in entries)
            {
                SnapshotEntries.Add(new SnapshotEntryDisplayModel
                {
                    ChangesetId = e.ChangesetId,
                    ModId = e.ModId ?? "Unknown",
                    ArchiveName = !string.IsNullOrEmpty(e.ArchivePath) ? Path.GetFileName(e.ArchivePath) : "N/A",
                    InstalledAt = FormatTimestamp(e.ChangesetCreatedAtUtc)
                });
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading entries: {ex.Message}";
        }
    }

    private void BuildCalendarGrid()
    {
        CalendarDays.Clear();
        SelectedDay = null;
        DaySnapshots.Clear();
        SelectedSnapshot = null;
        SnapshotEntries.Clear();

        var firstOfMonth = new DateTime(DisplayYear, DisplayMonth, 1);
        var daysInMonth = DateTime.DaysInMonth(DisplayYear, DisplayMonth);
        var startDayOfWeek = (int)firstOfMonth.DayOfWeek; // 0=Sunday
        var today = DateTime.UtcNow.Date;

        // Fill leading days from previous month
        var prevMonth = firstOfMonth.AddMonths(-1);
        var prevDays = DateTime.DaysInMonth(prevMonth.Year, prevMonth.Month);
        for (int i = startDayOfWeek - 1; i >= 0; i--)
        {
            var dayNum = prevDays - i;
            CalendarDays.Add(new CalendarDayModel
            {
                DayNumber = dayNum,
                IsCurrentMonth = false,
                Date = new DateTime(prevMonth.Year, prevMonth.Month, dayNum)
            });
        }

        // Current month days
        for (int d = 1; d <= daysInMonth; d++)
        {
            var date = new DateTime(DisplayYear, DisplayMonth, d);
            CalendarDays.Add(new CalendarDayModel
            {
                DayNumber = d,
                IsCurrentMonth = true,
                IsToday = date == today,
                Date = date
            });
        }

        // Fill trailing days to reach 42 cells (6 rows x 7 columns)
        var nextMonth = firstOfMonth.AddMonths(1);
        var remaining = 42 - CalendarDays.Count;
        for (int d = 1; d <= remaining; d++)
        {
            CalendarDays.Add(new CalendarDayModel
            {
                DayNumber = d,
                IsCurrentMonth = false,
                Date = new DateTime(nextMonth.Year, nextMonth.Month, d)
            });
        }
    }

    private void UpdateDisplayMonthName()
    {
        DisplayMonthName = new DateTime(DisplayYear, DisplayMonth, 1).ToString("MMMM yyyy", CultureInfo.CurrentCulture);
    }

    private static string FormatTimestamp(string isoTimestamp)
    {
        if (DateTime.TryParse(isoTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
            return dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        return isoTimestamp;
    }
}

public partial class CalendarDayModel : ObservableObject
{
    [ObservableProperty]
    private int _dayNumber;

    [ObservableProperty]
    private bool _isCurrentMonth;

    [ObservableProperty]
    private bool _isToday;

    [ObservableProperty]
    private bool _hasSnapshots;

    [ObservableProperty]
    private DateTime _date;

    public double DayOpacity => IsCurrentMonth ? 1.0 : 0.3;
    public Avalonia.Media.FontWeight DayFontWeight => IsToday ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.Normal;
}

public partial class SnapshotDisplayModel : ObservableObject
{
    [ObservableProperty]
    private string _snapshotId = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _createdAt = string.Empty;

    [ObservableProperty]
    private int _modCount;

    [ObservableProperty]
    private string _trigger = string.Empty;

    [ObservableProperty]
    private string _gameName = string.Empty;
}

public partial class SnapshotEntryDisplayModel : ObservableObject
{
    [ObservableProperty]
    private string _changesetId = string.Empty;

    [ObservableProperty]
    private string _modId = string.Empty;

    [ObservableProperty]
    private string _archiveName = string.Empty;

    [ObservableProperty]
    private string _installedAt = string.Empty;
}
