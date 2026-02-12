using System.Text.Json;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Configuration;
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

    [ObservableProperty]
    private string _nexusApiKey = string.Empty;

    [ObservableProperty]
    private bool _showNexusApiKey;

    [ObservableProperty]
    private string _gameBananaUserId = string.Empty;

    [ObservableProperty]
    private string _modsDirectory = string.Empty;

    [ObservableProperty]
    private bool _verifyDownloads = true;

    [ObservableProperty]
    private string _selectedTheme = "System";

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _hasUnsavedChanges;

    public string[] AvailableThemes { get; } = { "System", "Light", "Dark" };

    // Designer constructor
    public SettingsViewModel()
    {
        NexusApiKey = "your-api-key-here";
        GameBananaUserId = "12345";
        ModsDirectory = "~/Games/Mods-Lists";
    }

    // DI constructor
    public SettingsViewModel(
        ConfigurationService configService,
        AppSettings settings,
        IDialogService dialogService)
    {
        _configService = configService;
        _settings = settings;
        _dialogService = dialogService;

        LoadSettings();
    }

    private void LoadSettings()
    {
        if (_settings == null) return;

        NexusApiKey = _settings.NexusApiKey ?? string.Empty;
        GameBananaUserId = _settings.GameBananaUserId ?? string.Empty;
        ModsDirectory = _settings.ModsDirectory ?? string.Empty;
        VerifyDownloads = _settings.VerifyDownloads;

        HasUnsavedChanges = false;
        StatusMessage = string.Empty;
    }

    partial void OnNexusApiKeyChanged(string value) => HasUnsavedChanges = true;
    partial void OnGameBananaUserIdChanged(string value) => HasUnsavedChanges = true;
    partial void OnModsDirectoryChanged(string value) => HasUnsavedChanges = true;
    partial void OnVerifyDownloadsChanged(bool value) => HasUnsavedChanges = true;

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
            _settings.NexusApiKey = NexusApiKey;
            _settings.GameBananaUserId = GameBananaUserId;
            _settings.ModsDirectory = ModsDirectory;
            _settings.VerifyDownloads = VerifyDownloads;

            await _configService.SaveAsync(_settings);

            HasUnsavedChanges = false;
            StatusMessage = "Settings saved successfully";
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
}
