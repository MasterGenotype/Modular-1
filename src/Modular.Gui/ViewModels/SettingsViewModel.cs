using System.Text.Json;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using System.Collections.ObjectModel;
using Modular.Core.Authentication;
using Modular.Core.Configuration;
using Modular.Core.Diagnostics;
using Modular.Core.Telemetry;
using Modular.Gui.Messages;
using Modular.Gui.Services;

namespace Modular.Gui.ViewModels;

/// <summary>
/// ViewModel for the Settings view.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly ConfigurationService? _configService;
    private readonly AppSettings? _settings;
    private readonly IDialogService? _dialogService;
    private readonly NexusSsoClient? _ssoClient;
    private readonly DiagnosticService? _diagnosticService;
    private readonly TelemetryService? _telemetryService;

    // NexusMods settings
    [ObservableProperty]
    private string _nexusApiKey = string.Empty;

    [ObservableProperty]
    private bool _showNexusApiKey;

    [ObservableProperty]
    private string _nexusApplicationSlug = "vortex";

    [ObservableProperty]
    private bool _nexusSsoEnabled = true;

    // GameBanana settings
    [ObservableProperty]
    private string _gameBananaUserId = string.Empty;

    [ObservableProperty]
    private string _gameBananaGameIds = string.Empty;

    [ObservableProperty]
    private string _gameBananaDownloadDir = "gamebanana";

    // Backend settings
    [ObservableProperty]
    private bool _nexusModsBackendEnabled = true;

    [ObservableProperty]
    private bool _gameBananaBackendEnabled = true;

    // Download settings
    [ObservableProperty]
    private string _modsDirectory = string.Empty;

    [ObservableProperty]
    private string _defaultCategories = "main, optional";

    [ObservableProperty]
    private bool _autoRename = true;

    [ObservableProperty]
    private bool _organizeByCategory = true;

    [ObservableProperty]
    private bool _verifyDownloads = true;

    [ObservableProperty]
    private bool _validateTracking;

    [ObservableProperty]
    private int _maxConcurrentDownloads = 1;

    // Advanced settings
    [ObservableProperty]
    private bool _verbose;

    [ObservableProperty]
    private string _cookieFile = string.Empty;

    [ObservableProperty]
    private string _databasePath = string.Empty;

    [ObservableProperty]
    private string _rateLimitStatePath = string.Empty;

    [ObservableProperty]
    private string _metadataCachePath = string.Empty;

    // Sub-page navigation: "Main", "Profiles", "Plugins"
    [ObservableProperty]
    private string _currentSubPage = "Main";

    [ObservableProperty]
    private bool _showMainSettings = true;

    [ObservableProperty]
    private bool _showProfilesPage;

    [ObservableProperty]
    private bool _showPluginsPage;

    // Child ViewModels for sub-pages
    public ProfilesViewModel? ProfilesViewModel { get; }
    public PluginsViewModel? PluginsViewModel { get; }

    // UI state
    [ObservableProperty]
    private string _selectedTheme = "System";

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _hasUnsavedChanges;

    // SSO Login state
    [ObservableProperty]
    private bool _isSsoLoggingIn;

    [ObservableProperty]
    private string _ssoStatusMessage = string.Empty;

    // Diagnostics state
    [ObservableProperty]
    private bool _isRunningDiagnostics;

    [ObservableProperty]
    private ObservableCollection<DiagnosticCheckItem> _diagnosticResults = new();

    [ObservableProperty]
    private string _diagnosticOverallStatus = string.Empty;

    [ObservableProperty]
    private string _diagnosticSystemInfo = string.Empty;

    // Telemetry state
    [ObservableProperty]
    private string _telemetrySummaryText = string.Empty;

    [ObservableProperty]
    private bool _isLoadingTelemetry;

    public string[] AvailableThemes { get; } = { "System", "Light", "Dark" };
    public int[] ConcurrentDownloadOptions { get; } = { 1, 2, 3, 4, 5 };

    // Designer constructor
    public SettingsViewModel()
    {
        NexusApiKey = "your-api-key-here";
        GameBananaUserId = "12345";
        ModsDirectory = "~/Games/Mods-Lists";
        ProfilesViewModel = new ProfilesViewModel();
        PluginsViewModel = new PluginsViewModel();
    }

    // DI constructor
    public SettingsViewModel(
        ConfigurationService configService,
        AppSettings settings,
        IDialogService dialogService,
        NexusSsoClient ssoClient,
        DiagnosticService diagnosticService,
        TelemetryService telemetryService,
        ProfilesViewModel profilesViewModel,
        PluginsViewModel pluginsViewModel)
    {
        _configService = configService;
        _settings = settings;
        _dialogService = dialogService;
        _ssoClient = ssoClient;
        _diagnosticService = diagnosticService;
        _telemetryService = telemetryService;
        ProfilesViewModel = profilesViewModel;
        PluginsViewModel = pluginsViewModel;

        LoadSettings();
    }

    private void LoadSettings()
    {
        if (_settings == null) return;

        // NexusMods
        NexusApiKey = _settings.NexusApiKey ?? string.Empty;
        NexusApplicationSlug = _settings.NexusApplicationSlug ?? "vortex";
        NexusSsoEnabled = _settings.NexusSsoEnabled;

        // GameBanana
        GameBananaUserId = _settings.GameBananaUserId ?? string.Empty;
        GameBananaGameIds = _settings.GameBananaGameIds.Count > 0
            ? string.Join(", ", _settings.GameBananaGameIds)
            : string.Empty;
        GameBananaDownloadDir = _settings.GameBananaDownloadDir ?? "gamebanana";

        // Backends
        NexusModsBackendEnabled = _settings.EnabledBackends.Contains("nexusmods");
        GameBananaBackendEnabled = _settings.EnabledBackends.Contains("gamebanana");

        // Downloads
        ModsDirectory = _settings.ModsDirectory ?? string.Empty;
        DefaultCategories = _settings.DefaultCategories.Count > 0
            ? string.Join(", ", _settings.DefaultCategories)
            : "main, optional";
        AutoRename = _settings.AutoRename;
        OrganizeByCategory = _settings.OrganizeByCategory;
        VerifyDownloads = _settings.VerifyDownloads;
        ValidateTracking = _settings.ValidateTracking;
        MaxConcurrentDownloads = _settings.MaxConcurrentDownloads;

        // Advanced
        Verbose = _settings.Verbose;
        CookieFile = _settings.CookieFile ?? string.Empty;
        DatabasePath = _settings.DatabasePath ?? string.Empty;
        RateLimitStatePath = _settings.RateLimitStatePath ?? string.Empty;
        MetadataCachePath = _settings.MetadataCachePath ?? string.Empty;

        HasUnsavedChanges = false;
        StatusMessage = string.Empty;
    }

    // Change handlers
    partial void OnNexusApiKeyChanged(string value) => HasUnsavedChanges = true;
    partial void OnNexusApplicationSlugChanged(string value) => HasUnsavedChanges = true;
    partial void OnNexusSsoEnabledChanged(bool value) => HasUnsavedChanges = true;
    partial void OnGameBananaUserIdChanged(string value) => HasUnsavedChanges = true;
    partial void OnGameBananaGameIdsChanged(string value) => HasUnsavedChanges = true;
    partial void OnGameBananaDownloadDirChanged(string value) => HasUnsavedChanges = true;
    partial void OnNexusModsBackendEnabledChanged(bool value) => HasUnsavedChanges = true;
    partial void OnGameBananaBackendEnabledChanged(bool value) => HasUnsavedChanges = true;
    partial void OnModsDirectoryChanged(string value) => HasUnsavedChanges = true;
    partial void OnDefaultCategoriesChanged(string value) => HasUnsavedChanges = true;
    partial void OnAutoRenameChanged(bool value) => HasUnsavedChanges = true;
    partial void OnOrganizeByCategoryChanged(bool value) => HasUnsavedChanges = true;
    partial void OnVerifyDownloadsChanged(bool value) => HasUnsavedChanges = true;
    partial void OnValidateTrackingChanged(bool value) => HasUnsavedChanges = true;
    partial void OnMaxConcurrentDownloadsChanged(int value) => HasUnsavedChanges = true;
    partial void OnVerboseChanged(bool value) => HasUnsavedChanges = true;
    partial void OnCookieFileChanged(string value) => HasUnsavedChanges = true;
    partial void OnDatabasePathChanged(string value) => HasUnsavedChanges = true;
    partial void OnRateLimitStatePathChanged(string value) => HasUnsavedChanges = true;
    partial void OnMetadataCachePathChanged(string value) => HasUnsavedChanges = true;

    partial void OnCurrentSubPageChanged(string value)
    {
        ShowMainSettings = value == "Main";
        ShowProfilesPage = value == "Profiles";
        ShowPluginsPage = value == "Plugins";
    }

    [RelayCommand]
    private void NavigateToSubPage(string page)
    {
        CurrentSubPage = page;
    }

    [RelayCommand]
    private void NavigateBack()
    {
        CurrentSubPage = "Main";
    }

    partial void OnSelectedThemeChanged(string value)
    {
        ApplyTheme(value);
    }

    private void ApplyTheme(string theme)
    {
        if (Application.Current == null) return;

        Application.Current.RequestedThemeVariant = theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }

    [RelayCommand]
    private void ToggleApiKeyVisibility()
    {
        ShowNexusApiKey = !ShowNexusApiKey;
    }

    [RelayCommand]
    private async Task BrowseModsDirectoryAsync()
    {
        if (_dialogService == null) return;

        var folder = await _dialogService.ShowFolderBrowserAsync(
            "Select Mods Directory",
            ModsDirectory);

        if (!string.IsNullOrEmpty(folder))
        {
            ModsDirectory = folder;
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (_configService == null || _settings == null)
        {
            StatusMessage = "Configuration service not available";
            return;
        }

        try
        {
            // NexusMods
            _settings.NexusApiKey = NexusApiKey;
            _settings.NexusApplicationSlug = NexusApplicationSlug;
            _settings.NexusSsoEnabled = NexusSsoEnabled;

            // GameBanana
            _settings.GameBananaUserId = GameBananaUserId;
            _settings.GameBananaGameIds = ParseIntList(GameBananaGameIds);
            _settings.GameBananaDownloadDir = GameBananaDownloadDir;

            // Backends
            _settings.EnabledBackends = BuildEnabledBackends();

            // Downloads
            _settings.ModsDirectory = ModsDirectory;
            _settings.DefaultCategories = ParseStringList(DefaultCategories);
            _settings.AutoRename = AutoRename;
            _settings.OrganizeByCategory = OrganizeByCategory;
            _settings.VerifyDownloads = VerifyDownloads;
            _settings.ValidateTracking = ValidateTracking;
            _settings.MaxConcurrentDownloads = MaxConcurrentDownloads;

            // Advanced
            _settings.Verbose = Verbose;
            _settings.CookieFile = CookieFile;
            _settings.DatabasePath = DatabasePath;
            _settings.RateLimitStatePath = RateLimitStatePath;
            _settings.MetadataCachePath = MetadataCachePath;

            await _configService.SaveAsync(_settings);

            HasUnsavedChanges = false;
            StatusMessage = "Settings saved successfully";

            WeakReferenceMessenger.Default.Send(new SettingsChangedMessage(
                new SettingsChangedInfo { SettingName = "all" }));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error saving settings: {ex.Message}";
            if (_dialogService != null)
            {
                await _dialogService.ShowErrorAsync("Error", $"Failed to save settings: {ex.Message}");
            }
        }
    }

    [RelayCommand]
    private void ResetSettings()
    {
        LoadSettings();
        StatusMessage = "Settings reset to last saved values";
    }

    [ObservableProperty]
    private bool _isTestingConnection;

    private static List<int> ParseIntList(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var i) ? i : (int?)null)
            .Where(i => i.HasValue)
            .Select(i => i!.Value)
            .ToList();
    }

    private static List<string> ParseStringList(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private List<string> BuildEnabledBackends()
    {
        var backends = new List<string>();
        if (NexusModsBackendEnabled) backends.Add("nexusmods");
        if (GameBananaBackendEnabled) backends.Add("gamebanana");
        return backends;
    }

    [RelayCommand]
    private async Task TestNexusConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(NexusApiKey))
        {
            StatusMessage = "Please enter an API key first";
            return;
        }

        IsTestingConnection = true;
        StatusMessage = "Testing connection...";

        try
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("apikey", NexusApiKey);
            httpClient.DefaultRequestHeaders.Add("accept", "application/json");

            var response = await httpClient.GetAsync("https://api.nexusmods.com/v1/users/validate.json");

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var userName = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : "Unknown";
                var isPremium = root.TryGetProperty("is_premium", out var premiumProp) && premiumProp.GetBoolean();

                StatusMessage = $"✓ Connected as: {userName}" + (isPremium ? " (Premium)" : "");
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                StatusMessage = "✗ Invalid API key";
            }
            else
            {
                StatusMessage = $"✗ Connection failed: {response.StatusCode}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"✗ Connection error: {ex.Message}";
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    [RelayCommand]
    private async Task LoginViaSsoAsync()
    {
        if (_ssoClient == null)
        {
            SsoStatusMessage = "SSO client not available";
            return;
        }

        IsSsoLoggingIn = true;
        SsoStatusMessage = "Opening browser for NexusMods login...";

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var apiKey = await _ssoClient.AuthenticateAsync(cts.Token);

            NexusApiKey = apiKey;
            HasUnsavedChanges = true;
            SsoStatusMessage = "Login successful! Save settings to persist the API key.";
        }
        catch (OperationCanceledException)
        {
            SsoStatusMessage = "Login timed out — please try again";
        }
        catch (Exception ex)
        {
            SsoStatusMessage = $"Login failed: {ex.Message}";
        }
        finally
        {
            IsSsoLoggingIn = false;
        }
    }

    [RelayCommand]
    private async Task RunDiagnosticsAsync()
    {
        if (_diagnosticService == null)
        {
            DiagnosticOverallStatus = "Diagnostic service not available";
            return;
        }

        IsRunningDiagnostics = true;
        DiagnosticResults.Clear();
        DiagnosticOverallStatus = "Running health checks...";

        try
        {
            var report = await _diagnosticService.RunHealthCheckAsync();

            DiagnosticOverallStatus = $"Overall: {report.OverallStatus} ({report.Timestamp:g})";

            foreach (var check in report.Checks)
            {
                DiagnosticResults.Add(new DiagnosticCheckItem
                {
                    Name = check.Name,
                    Status = check.Status.ToString(),
                    Message = check.Message
                });
            }

            // Add system info
            var sysReport = _diagnosticService.GenerateReport();
            DiagnosticSystemInfo = $"Runtime: {sysReport.Runtime} | Platform: {sysReport.Platform} | " +
                                   $"Plugins: {sysReport.LoadedPlugins.Count} | " +
                                   $"Installers: {sysReport.TotalInstallers}";
        }
        catch (Exception ex)
        {
            DiagnosticOverallStatus = $"Error: {ex.Message}";
        }
        finally
        {
            IsRunningDiagnostics = false;
        }
    }

    [RelayCommand]
    private void LoadTelemetrySummary()
    {
        if (_telemetryService == null)
        {
            TelemetrySummaryText = "Telemetry service not available";
            return;
        }

        IsLoadingTelemetry = true;

        try
        {
            var summary = _telemetryService.GetSummary();

            TelemetrySummaryText = $"Events: {summary.TotalEvents} | " +
                                  $"Downloads: {summary.TotalDownloads} | " +
                                  $"Installer OK: {summary.InstallerSuccesses} | " +
                                  $"Installer Fail: {summary.InstallerFailures} | " +
                                  $"Plugin Crashes: {summary.PluginCrashes}";
        }
        catch (Exception ex)
        {
            TelemetrySummaryText = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoadingTelemetry = false;
        }
    }

    [RelayCommand]
    private async Task ExportTelemetryAsync()
    {
        if (_telemetryService == null || _dialogService == null) return;

        var folder = await _dialogService.ShowFolderBrowserAsync("Select Export Location");
        if (string.IsNullOrEmpty(folder)) return;

        var outputPath = Path.Combine(folder, $"telemetry_export_{DateTime.Now:yyyyMMdd_HHmmss}.json");

        try
        {
            var success = await _telemetryService.ExportDataAsync(outputPath);
            StatusMessage = success ? $"Telemetry exported to {outputPath}" : "Export failed";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ClearTelemetryAsync()
    {
        if (_telemetryService == null || _dialogService == null) return;

        var confirmed = await _dialogService.ShowConfirmationAsync(
            "Clear Telemetry",
            "Are you sure you want to clear all telemetry data? This action cannot be undone.");

        if (!confirmed) return;

        try
        {
            _telemetryService.ClearData();
            TelemetrySummaryText = string.Empty;
            StatusMessage = "Telemetry data cleared";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error clearing telemetry: {ex.Message}";
        }
    }
}

public partial class DiagnosticCheckItem : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;
}
