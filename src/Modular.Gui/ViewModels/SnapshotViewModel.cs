using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.GameDetection;
using Modular.Core.Installers;
using Modular.Core.Snapshots;
using Modular.Gui.Services;
using Modular.Sdk.Installers;

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
    private string _daySnapshotsHeader = "Snapshots";

    [ObservableProperty]
    private SnapshotRestorePlan? _restorePlan;

    [ObservableProperty]
    private string _snapshotSummary = string.Empty;

    [ObservableProperty]
    private string _restoreSummary = string.Empty;

    [ObservableProperty]
    private string _restoreWarning = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "Select a game to view snapshots";

    // Designer constructor
    public SnapshotViewModel()
    {
        var now = DateTime.Now;
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

        var now = DateTime.Now;
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
            var now = DateTime.Now;
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
            ClearSnapshotDetail();
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

        var plan = await _snapshotManager.PlanRestoreAsync(snapshot.SnapshotId);
        var message = $"Restore snapshot \"{snapshot.Name}\"?\n\n" +
            (plan == null
                ? "This will remove mods installed after this snapshot and reinstall any missing mods."
                : DescribeRestore(plan) +
                  (plan.Count(SnapshotRestoreAction.Unavailable) > 0 ? "\n\n" + DescribeUnavailable(plan) : ""));

        var confirmed = await _dialogService.ShowConfirmationAsync("Restore Snapshot", message);

        if (!confirmed) return;

        IsLoading = true;
        StatusMessage = "Restoring snapshot...";

        try
        {
            var progress = new Progress<InstallProgress>(p =>
            {
                if (!string.IsNullOrEmpty(p.CurrentOperation))
                    StatusMessage = p.CurrentOperation;
            });

            var result = await _snapshotManager.RestoreSnapshotAsync(
                snapshot.SnapshotId, _installService, progress);

            var counts = $"{result.ModsReinstalled} installed, {result.ModsRemoved} removed";

            if (result.Success)
            {
                StatusMessage = $"Snapshot restored: {counts}";
                await _dialogService.ShowInfoAsync(
                    "Snapshot Restored",
                    $"\"{snapshot.Name}\" was restored.\n\n" +
                    $"{counts}." +
                    (plan == null ? "" : $" The game now has the snapshot's {plan.ModCount} mod(s)."));
            }
            else
            {
                var problems = result.Errors.Count > 0
                    ? result.Errors
                    : [result.Error ?? "Unknown error"];
                const int shown = 15;

                StatusMessage = $"Restore finished with {problems.Count} problem(s): {counts}";
                await _dialogService.ShowErrorAsync(
                    "Snapshot Restore Incomplete",
                    $"\"{snapshot.Name}\" was only partly restored: {counts}.\n\n" +
                    $"{problems.Count} problem(s):\n" +
                    string.Join("\n", problems.Take(shown).Select(e => "• " + e)) +
                    (problems.Count > shown ? $"\n…and {problems.Count - shown} more." : ""));
            }

            // Show the snapshot against the restored state; a full restore now reads as
            // every mod kept.
            if (SelectedSnapshot?.SnapshotId == snapshot.SnapshotId)
                await LoadSnapshotEntriesAsync(snapshot.SnapshotId);
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
                ClearSnapshotDetail();
            }
            DaySnapshotsHeader = $"Snapshots ({DaySnapshots.Count})";

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
        ClearSnapshotDetail();

        try
        {
            // The calendar is in local time, as are the times shown beside each snapshot.
            var startLocal = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Local);
            var startUtc = startLocal.ToUniversalTime();
            var endUtc = startLocal.AddDays(1).ToUniversalTime();
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

            DaySnapshotsHeader = $"Snapshots on {date:MMM d} ({snapshots.Count})";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading snapshots: {ex.Message}";
        }
    }

    private async Task LoadSnapshotEntriesAsync(string snapshotId)
    {
        if (_snapshotManager == null) return;

        ClearSnapshotDetail();

        try
        {
            var plan = await _snapshotManager.PlanRestoreAsync(snapshotId);

            // The user may have picked another snapshot while this one loaded.
            if (plan == null || SelectedSnapshot?.SnapshotId != snapshotId)
                return;

            RestorePlan = plan;
            SnapshotSummary = $"{plan.ModCount} mod(s) in this snapshot, {plan.EnabledCount} enabled";
            RestoreSummary = DescribeRestore(plan);
            RestoreWarning = plan.Count(SnapshotRestoreAction.Unavailable) > 0
                ? DescribeUnavailable(plan)
                : string.Empty;

            foreach (var item in plan.Items)
            {
                SnapshotEntries.Add(new SnapshotEntryDisplayModel
                {
                    ModId = item.DisplayName,
                    Enabled = item.Enabled switch { true => "Yes", false => "No", null => "" },
                    OnRestore = item.Action switch
                    {
                        SnapshotRestoreAction.Keep => "Keep",
                        SnapshotRestoreAction.Add => "Add",
                        SnapshotRestoreAction.Reinstall => "Reinstall",
                        SnapshotRestoreAction.Unavailable => "Missing archive",
                        SnapshotRestoreAction.Remove => "Remove",
                        _ => item.Action.ToString()
                    },
                    ArchiveName = !string.IsNullOrEmpty(item.ArchivePath) ? Path.GetFileName(item.ArchivePath) : ""
                });
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading snapshot: {ex.Message}";
        }
    }

    private void ClearSnapshotDetail()
    {
        SnapshotEntries.Clear();
        RestorePlan = null;
        SnapshotSummary = string.Empty;
        RestoreSummary = string.Empty;
        RestoreWarning = string.Empty;
    }

    private static string DescribeRestore(SnapshotRestorePlan plan)
    {
        var reinstall = plan.Count(SnapshotRestoreAction.Reinstall);
        var install = plan.InstallCount > 0
            ? $"install {plan.InstallCount}" + (reinstall > 0 ? $" ({reinstall} from archives)" : "")
            : "install nothing";

        return $"Restoring would {install}, remove {plan.Count(SnapshotRestoreAction.Remove)} " +
               $"and keep {plan.Count(SnapshotRestoreAction.Keep)}.";
    }

    private static string DescribeUnavailable(SnapshotRestorePlan plan) =>
        $"{plan.Count(SnapshotRestoreAction.Unavailable)} mod(s) can't be restored: " +
        "their staged files and archives are both gone.";

    private void BuildCalendarGrid()
    {
        CalendarDays.Clear();
        SelectedDay = null;
        DaySnapshots.Clear();
        DaySnapshotsHeader = "Snapshots";
        SelectedSnapshot = null;
        ClearSnapshotDetail();

        var firstOfMonth = new DateTime(DisplayYear, DisplayMonth, 1);
        var daysInMonth = DateTime.DaysInMonth(DisplayYear, DisplayMonth);
        var startDayOfWeek = (int)firstOfMonth.DayOfWeek; // 0=Sunday
        var today = DateTime.Now.Date;

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
    private string _modId = string.Empty;

    [ObservableProperty]
    private string _enabled = string.Empty;

    [ObservableProperty]
    private string _onRestore = string.Empty;

    [ObservableProperty]
    private string _archiveName = string.Empty;
}
