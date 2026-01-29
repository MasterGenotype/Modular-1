# GUI Implementation Recommendations for Modular

This document provides comprehensive recommendations for adding a graphical user interface (GUI) to the Modular mod manager application.

---

## Executive Summary

The Modular codebase is well-architected for GUI integration. The clean separation between the CLI layer (`Modular.Cli`) and the core business logic (`Modular.Core`) means a GUI can be implemented as a parallel presentation layer without modifying existing services. This document covers framework selection, architectural changes, UI/UX design, and a phased implementation roadmap.

---

## 1. Framework Selection

### 1.1 Recommended Framework: Avalonia UI

**Avalonia UI** is the recommended framework for the following reasons:

| Criteria | Avalonia UI | .NET MAUI | WPF | Electron/Webview |
|----------|-------------|-----------|-----|------------------|
| Cross-Platform (Win/Mac/Linux) | Yes | Partial (no Linux) | No (Windows only) | Yes |
| Native Look & Feel | Good | Good | Excellent | Poor |
| .NET 8.0 Support | Full | Full | Full | Via bridge |
| MVVM Support | Excellent | Good | Excellent | Manual |
| Community/Ecosystem | Growing | Large | Mature | Large |
| Performance | Excellent | Good | Excellent | Moderate |
| Package Size | ~15MB | ~30MB | N/A | ~100MB+ |
| Dark Mode Support | Built-in | Yes | Manual | Yes |

**Key Avalonia Benefits:**
- **True cross-platform**: Works on Linux, Windows, macOS, and even browser (WebAssembly)
- **XAML familiarity**: Similar syntax to WPF/UWP for developers familiar with those
- **Reactive UI integration**: First-class support for ReactiveUI/MVVM patterns
- **Active development**: Regular releases, strong community
- **Modern styling**: Fluent Design and custom themes available

### 1.2 Alternative Considerations

**If Linux support is not critical:**
- **.NET MAUI**: Better mobile support if future iOS/Android targets are desired
- **WPF**: Maximum Windows native integration and mature ecosystem

**For rapid prototyping:**
- **Terminal.Gui**: Enhanced terminal UI (stays in console paradigm)
- **Spectre.Console Widgets**: Extended console UI (already using Spectre.Console)

### 1.3 NuGet Packages Required

```xml
<!-- Core Avalonia -->
<PackageReference Include="Avalonia" Version="11.2.*" />
<PackageReference Include="Avalonia.Desktop" Version="11.2.*" />
<PackageReference Include="Avalonia.Themes.Fluent" Version="11.2.*" />
<PackageReference Include="Avalonia.Fonts.Inter" Version="11.2.*" />

<!-- MVVM Support -->
<PackageReference Include="CommunityToolkit.Mvvm" Version="8.3.*" />

<!-- Optional: ReactiveUI for advanced scenarios -->
<PackageReference Include="Avalonia.ReactiveUI" Version="11.2.*" />

<!-- Optional: Icons -->
<PackageReference Include="Material.Icons.Avalonia" Version="2.1.*" />
```

---

## 2. Architectural Changes

### 2.1 Project Structure

Create a new project `Modular.Gui` alongside the existing structure:

```
src/
├── Modular.Core/           # Existing - no changes needed
├── Modular.FluentHttp/     # Existing - no changes needed
├── Modular.Cli/            # Existing - CLI interface
└── Modular.Gui/            # NEW - GUI application
    ├── App.axaml
    ├── App.axaml.cs
    ├── Program.cs
    ├── ViewModels/
    │   ├── MainWindowViewModel.cs
    │   ├── ModListViewModel.cs
    │   ├── DownloadQueueViewModel.cs
    │   ├── SettingsViewModel.cs
    │   └── ViewModelBase.cs
    ├── Views/
    │   ├── MainWindow.axaml
    │   ├── ModListView.axaml
    │   ├── DownloadQueueView.axaml
    │   └── SettingsView.axaml
    ├── Models/
    │   ├── ModDisplayModel.cs
    │   └── DownloadItemModel.cs
    ├── Services/
    │   ├── IDialogService.cs
    │   ├── DialogService.cs
    │   ├── INavigationService.cs
    │   └── NavigationService.cs
    ├── Converters/
    │   ├── StatusToColorConverter.cs
    │   └── FileSizeConverter.cs
    └── Assets/
        ├── Icons/
        └── Styles/
```

### 2.2 Service Layer Abstraction

Extract interfaces from existing services (as recommended in CODEBASE_REVIEW_RECOMMENDATIONS.md):

```csharp
// Modular.Core/Services/Interfaces/INexusModsService.cs
public interface INexusModsService
{
    Task<IEnumerable<TrackedMod>> GetTrackedModsAsync(string? domain = null, CancellationToken ct = default);
    Task<IEnumerable<ModFile>> GetModFilesAsync(string domain, int modId, CancellationToken ct = default);
    Task DownloadModFileAsync(string domain, int modId, int fileId, string outputPath,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default);
}

// Modular.Core/Services/Interfaces/IGameBananaService.cs
public interface IGameBananaService
{
    Task<IEnumerable<SubscribedMod>> GetSubscribedModsAsync(CancellationToken ct = default);
    Task DownloadModAsync(SubscribedMod mod, string outputPath,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default);
}
```

### 2.3 Progress Reporting Enhancement

Enhance the existing `IProgress<T>` usage with a richer model:

```csharp
// Modular.Core/Models/DownloadProgress.cs
public record DownloadProgress
{
    public string FileName { get; init; } = string.Empty;
    public long BytesDownloaded { get; init; }
    public long TotalBytes { get; init; }
    public double Percentage => TotalBytes > 0 ? (double)BytesDownloaded / TotalBytes * 100 : 0;
    public TimeSpan? EstimatedTimeRemaining { get; init; }
    public double SpeedBytesPerSecond { get; init; }
    public DownloadState State { get; init; }
}

public enum DownloadState
{
    Queued,
    Downloading,
    Verifying,
    Completed,
    Failed,
    Paused,
    Cancelled
}
```

### 2.4 Dependency Injection Setup

Implement full DI container as recommended:

```csharp
// Modular.Gui/Program.cs
public static void Main(string[] args)
{
    var services = new ServiceCollection();

    // Core services
    services.AddSingleton<ConfigurationService>();
    services.AddSingleton<AppSettings>(sp => sp.GetRequiredService<ConfigurationService>().LoadAsync().Result);
    services.AddSingleton<IRateLimiter, NexusRateLimiter>();
    services.AddSingleton<DownloadDatabase>();

    // Business services
    services.AddTransient<INexusModsService, NexusModsService>();
    services.AddTransient<IGameBananaService, GameBananaService>();
    services.AddTransient<IRenameService, RenameService>();

    // GUI services
    services.AddSingleton<IDialogService, DialogService>();
    services.AddSingleton<INavigationService, NavigationService>();

    // ViewModels
    services.AddTransient<MainWindowViewModel>();
    services.AddTransient<ModListViewModel>();
    services.AddTransient<DownloadQueueViewModel>();
    services.AddTransient<SettingsViewModel>();

    var provider = services.BuildServiceProvider();

    BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);
}
```

---

## 3. UI/UX Design Recommendations

### 3.1 Main Window Layout

```
┌─────────────────────────────────────────────────────────────────────────────┐
│  [Logo] Modular                              [Settings] [Minimize] [Close]  │
├─────────────────────────────────────────────────────────────────────────────┤
│  ┌──────────────┐  ┌──────────────────────────────────────────────────────┐ │
│  │ Navigation   │  │ Content Area                                        │ │
│  │              │  │                                                      │ │
│  │ ▶ NexusMods  │  │ ┌────────────────────────────────────────────────┐  │ │
│  │   GameBanana │  │ │ Tracked Mods (skyrimspecialedition)         ▼│  │ │
│  │   Downloads  │  │ ├────────────────────────────────────────────────┤  │ │
│  │   Library    │  │ │ [Search: ________________] [Filter ▼] [Refresh]│  │ │
│  │   Settings   │  │ ├────────────────────────────────────────────────┤  │ │
│  │              │  │ │ ☐ │ Mod Name          │ Version │ Status       │  │ │
│  │              │  │ │ ☑ │ SkyUI             │ 5.2.1   │ ✓ Downloaded │  │ │
│  │              │  │ │ ☑ │ SKSE64           │ 2.0.21  │ ⬇ Update     │  │ │
│  │              │  │ │ ☐ │ RaceMenu         │ 0.4.19  │ ○ Not DL'd   │  │ │
│  │              │  │ │ ☑ │ Unofficial Patch │ 4.2.9   │ ✓ Downloaded │  │ │
│  │              │  │ │   │ ...              │         │              │  │ │
│  └──────────────┘  │ └────────────────────────────────────────────────┘  │ │
│                    │                                                      │ │
│                    │ [Download Selected] [Download All Updates]           │ │
│                    └──────────────────────────────────────────────────────┘ │
├─────────────────────────────────────────────────────────────────────────────┤
│  Download Queue                                                    [Clear] │
│  ┌─────────────────────────────────────────────────────────────────────────┤
│  │ SkyUI-5.2.1.zip          ████████████████████░░░░  78% │ 12.4 MB/s    │ │
│  │ SKSE64-2.0.21.zip        ░░░░░░░░░░░░░░░░░░░░░░░░  Queued             │ │
│  └─────────────────────────────────────────────────────────────────────────┤
├─────────────────────────────────────────────────────────────────────────────┤
│  Rate Limit: 18,432/20,000 daily │ 423/500 hourly │ Connected ●           │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 3.2 Key Views

#### 3.2.1 Mod List View (Primary)
- **Grid/Table display** with sortable columns
- **Thumbnail support** (lazy-loaded from NexusMods/GameBanana)
- **Multi-select** with checkbox column
- **Context menu**: Download, Update, View on Website, Remove from Tracking
- **Status indicators**: Downloaded, Update Available, Not Downloaded, Downloading
- **Search/Filter bar**: By name, category, game domain, status

#### 3.2.2 Download Queue View
- **Active downloads** with real-time progress bars
- **Speed indicator** (MB/s) with graph option
- **Queue management**: Pause, Resume, Cancel, Reorder (drag & drop)
- **Download history** tab with success/failure indicators

#### 3.2.3 Library View
- **Downloaded mods** organized by game/category
- **File browser** showing actual files on disk
- **Actions**: Open folder, Rename, Delete, Verify integrity

#### 3.2.4 Settings View
- **API Keys**: Secure input fields with reveal toggle
- **Paths**: Mods directory picker with folder browser dialog
- **Preferences**: Default categories, auto-rename, verify downloads
- **Rate Limits**: Current status, daily/hourly remaining
- **Appearance**: Theme selection (Light/Dark/System)

### 3.3 Design Principles

1. **Responsive Layout**: Use DockPanel/Grid for fluid resizing
2. **Consistent Iconography**: Use Material Design icons throughout
3. **Progress Feedback**: Always show progress for async operations
4. **Keyboard Navigation**: Full keyboard support with shortcuts
5. **Error Handling**: User-friendly error dialogs with retry options
6. **Accessibility**: High contrast support, screen reader compatibility

### 3.4 Theme/Styling

```xml
<!-- App.axaml - Fluent theme with customization -->
<Application.Styles>
    <FluentTheme />
    <StyleInclude Source="avares://Modular.Gui/Assets/Styles/ModularTheme.axaml" />
</Application.Styles>

<Application.Resources>
    <Color x:Key="PrimaryColor">#0078D4</Color>
    <Color x:Key="SuccessColor">#107C10</Color>
    <Color x:Key="WarningColor">#FFB900</Color>
    <Color x:Key="ErrorColor">#D83B01</Color>
</Application.Resources>
```

---

## 4. Key ViewModels

### 4.1 ModListViewModel

```csharp
public partial class ModListViewModel : ViewModelBase
{
    private readonly INexusModsService _nexusService;
    private readonly IGameBananaService _gameBananaService;
    private readonly DownloadDatabase _database;

    [ObservableProperty]
    private ObservableCollection<ModDisplayModel> _mods = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedDomain = "skyrimspecialedition";

    [ObservableProperty]
    private bool _isLoading;

    public ICollectionView FilteredMods { get; }

    [RelayCommand]
    private async Task RefreshModsAsync()
    {
        IsLoading = true;
        try
        {
            var trackedMods = await _nexusService.GetTrackedModsAsync(SelectedDomain);
            Mods.Clear();
            foreach (var mod in trackedMods)
            {
                var downloaded = _database.FindRecord(SelectedDomain, mod.ModId, /* latest file */) != null;
                Mods.Add(new ModDisplayModel(mod) { IsDownloaded = downloaded });
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task DownloadSelectedAsync()
    {
        var selected = Mods.Where(m => m.IsSelected && !m.IsDownloaded);
        foreach (var mod in selected)
        {
            // Add to download queue
            await DownloadQueueViewModel.EnqueueAsync(mod);
        }
    }
}
```

### 4.2 DownloadQueueViewModel

```csharp
public partial class DownloadQueueViewModel : ViewModelBase
{
    private readonly ConcurrentQueue<DownloadItemModel> _queue = new();
    private readonly SemaphoreSlim _downloadSemaphore;

    [ObservableProperty]
    private ObservableCollection<DownloadItemModel> _activeDownloads = new();

    [ObservableProperty]
    private ObservableCollection<DownloadItemModel> _completedDownloads = new();

    public async Task EnqueueAsync(ModDisplayModel mod)
    {
        var item = new DownloadItemModel(mod);
        _queue.Enqueue(item);
        ActiveDownloads.Add(item);
        await ProcessQueueAsync();
    }

    private async Task ProcessQueueAsync()
    {
        while (_queue.TryDequeue(out var item))
        {
            await _downloadSemaphore.WaitAsync();
            try
            {
                var progress = new Progress<DownloadProgress>(p =>
                {
                    item.Progress = p.Percentage;
                    item.Speed = p.SpeedBytesPerSecond;
                    item.State = p.State;
                });

                await _nexusService.DownloadModFileAsync(
                    item.Domain, item.ModId, item.FileId,
                    _settings.ModsDirectory, progress);

                item.State = DownloadState.Completed;
                CompletedDownloads.Insert(0, item);
            }
            catch (Exception ex)
            {
                item.State = DownloadState.Failed;
                item.ErrorMessage = ex.Message;
            }
            finally
            {
                _downloadSemaphore.Release();
                ActiveDownloads.Remove(item);
            }
        }
    }
}
```

---

## 5. Special Considerations

### 5.1 Thread Safety & UI Updates

Avalonia requires UI updates on the UI thread. Use `Dispatcher`:

```csharp
// For progress updates from background threads
Dispatcher.UIThread.Post(() =>
{
    ProgressValue = newValue;
});

// Or async
await Dispatcher.UIThread.InvokeAsync(() =>
{
    Mods.Add(newMod);
});
```

### 5.2 Cancellation Support

Implement proper cancellation for long-running operations:

```csharp
private CancellationTokenSource? _downloadCts;

[RelayCommand]
private async Task StartDownloadsAsync()
{
    _downloadCts = new CancellationTokenSource();
    try
    {
        await DownloadAllAsync(_downloadCts.Token);
    }
    catch (OperationCanceledException)
    {
        // Handle cancellation gracefully
    }
}

[RelayCommand]
private void CancelDownloads()
{
    _downloadCts?.Cancel();
}
```

### 5.3 Error Handling

Create a centralized error handling service:

```csharp
public interface IDialogService
{
    Task ShowErrorAsync(string title, string message);
    Task ShowWarningAsync(string title, string message);
    Task<bool> ShowConfirmationAsync(string title, string message);
    Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null);
}
```

### 5.4 Settings Persistence

Extend existing `ConfigurationService` for GUI-specific settings:

```csharp
public class GuiSettings
{
    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 800;
    public bool WindowMaximized { get; set; }
    public string Theme { get; set; } = "System"; // Light, Dark, System
    public string LastSelectedDomain { get; set; } = "skyrimspecialedition";
    public int[] ColumnWidths { get; set; } = { 300, 100, 150 };
}
```

### 5.5 Image/Thumbnail Handling

Implement lazy loading for mod thumbnails:

```csharp
public class ThumbnailService
{
    private readonly ConcurrentDictionary<string, Bitmap?> _cache = new();
    private readonly HttpClient _httpClient;

    public async Task<Bitmap?> GetThumbnailAsync(string url, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(url, out var cached))
            return cached;

        try
        {
            var bytes = await _httpClient.GetByteArrayAsync(url, ct);
            using var stream = new MemoryStream(bytes);
            var bitmap = new Bitmap(stream);
            _cache[url] = bitmap;
            return bitmap;
        }
        catch
        {
            _cache[url] = null; // Cache failure to avoid repeated requests
            return null;
        }
    }
}
```

---

## 6. Implementation Roadmap

### Phase 1: Foundation (Week 1-2)
- [ ] Create `Modular.Gui` project with Avalonia template
- [ ] Set up project structure (Views, ViewModels, Services)
- [ ] Implement dependency injection container
- [ ] Extract service interfaces from `Modular.Core`
- [ ] Create basic MainWindow with navigation shell
- [ ] Implement settings view (reuse existing ConfigurationService)

### Phase 2: Core Functionality (Week 3-4)
- [ ] Implement ModListView for NexusMods tracked mods
- [ ] Add mod list filtering, sorting, and search
- [ ] Create DownloadQueueView with progress tracking
- [ ] Implement download queue management (pause, cancel, reorder)
- [ ] Add rate limit status display in status bar

### Phase 3: Polish & Features (Week 5-6)
- [ ] Add GameBanana support view
- [ ] Implement Library view (downloaded mods browser)
- [ ] Add mod thumbnails with lazy loading
- [ ] Implement theme switching (Light/Dark/System)
- [ ] Add keyboard shortcuts and accessibility features
- [ ] Error handling and user-friendly error dialogs

### Phase 4: Advanced Features (Week 7-8)
- [ ] Implement mod update checking with visual indicators
- [ ] Add download history with statistics
- [ ] Create export/import settings functionality
- [ ] Add system tray integration (optional background downloading)
- [ ] Implement drag-and-drop for queue reordering
- [ ] Performance optimization and testing

### Phase 5: Release Preparation (Week 9-10)
- [ ] Cross-platform testing (Linux, Windows, macOS)
- [ ] Create installers for each platform
- [ ] Write user documentation
- [ ] Add auto-update mechanism
- [ ] Final bug fixes and polish

---

## 7. Build & Distribution

### 7.1 Project File

```xml
<!-- Modular.Gui/Modular.Gui.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <BuiltInComInteropSupport>true</BuiltInComInteropSupport>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Modular.Core\Modular.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Avalonia" Version="11.2.1" />
    <PackageReference Include="Avalonia.Desktop" Version="11.2.1" />
    <PackageReference Include="Avalonia.Themes.Fluent" Version="11.2.1" />
    <PackageReference Include="Avalonia.Fonts.Inter" Version="11.2.1" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.3.2" />
    <PackageReference Include="Material.Icons.Avalonia" Version="2.1.10" />
  </ItemGroup>
</Project>
```

### 7.2 Publishing

```bash
# Linux (self-contained)
dotnet publish src/Modular.Gui -c Release -r linux-x64 --self-contained -o publish/linux

# Windows (self-contained)
dotnet publish src/Modular.Gui -c Release -r win-x64 --self-contained -o publish/windows

# macOS (self-contained)
dotnet publish src/Modular.Gui -c Release -r osx-x64 --self-contained -o publish/macos

# Framework-dependent (smaller, requires .NET runtime)
dotnet publish src/Modular.Gui -c Release -o publish/fdd
```

### 7.3 Makefile Integration

```makefile
# Add to existing Makefile
gui:
	dotnet build src/Modular.Gui -c Release

gui-run:
	dotnet run --project src/Modular.Gui

gui-publish-linux:
	dotnet publish src/Modular.Gui -c Release -r linux-x64 --self-contained -o $(PUBLISH_DIR)/gui-linux

gui-publish-windows:
	dotnet publish src/Modular.Gui -c Release -r win-x64 --self-contained -o $(PUBLISH_DIR)/gui-windows
```

---

## 8. Alternative: Enhanced Terminal UI

If a full GUI is not immediately needed, consider enhancing the existing Spectre.Console UI:

### 8.1 Terminal.Gui Option

```csharp
// Using Terminal.Gui for a TUI (Text User Interface)
using Terminal.Gui;

Application.Run<ModularWindow>();

public class ModularWindow : Window
{
    private ListView _modList;
    private ProgressBar _downloadProgress;

    public ModularWindow()
    {
        Title = "Modular Mod Manager";

        var menuBar = new MenuBar(new MenuBarItem[] {
            new MenuBarItem("_File", new MenuItem[] {
                new MenuItem("_Settings", "", ShowSettings),
                new MenuItem("_Quit", "", () => Application.RequestStop()),
            }),
        });

        _modList = new ListView { X = 0, Y = 1, Width = Dim.Percent(70), Height = Dim.Fill(3) };
        _downloadProgress = new ProgressBar { X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1 };

        Add(menuBar, _modList, _downloadProgress);
    }
}
```

This provides a middle ground with:
- Rich interactive UI in the terminal
- Cross-platform compatibility
- Keyboard-driven navigation
- Less development effort than full GUI

---

## 9. Summary

### Recommended Approach

1. **Use Avalonia UI** as the GUI framework for true cross-platform support
2. **Maintain parallel CLI** - don't replace the CLI, add GUI as alternative
3. **Leverage existing services** - minimal changes to `Modular.Core`
4. **MVVM architecture** with CommunityToolkit.Mvvm for clean separation
5. **Phased implementation** - start with core functionality, add polish iteratively

### Key Success Factors

- **Reuse existing business logic** - The service layer is already well-designed
- **Proper async/await** - UI responsiveness is critical for download managers
- **User feedback** - Progress indicators, status updates, error messages
- **Cross-platform testing** - Test on all target platforms early

### Estimated Effort

| Phase | Duration | Complexity |
|-------|----------|------------|
| Foundation | 1-2 weeks | Medium |
| Core Functionality | 2 weeks | High |
| Polish & Features | 2 weeks | Medium |
| Advanced Features | 2 weeks | Medium |
| Release Preparation | 1-2 weeks | Low |
| **Total** | **8-10 weeks** | - |

The existing codebase architecture makes GUI integration straightforward. The clean separation of concerns means the GUI layer can focus purely on presentation while delegating all business logic to the existing, well-tested services.
