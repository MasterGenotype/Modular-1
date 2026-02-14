# Plugin Development Guide

This guide shows you how to create plugins for Modular.

## Overview

Modular supports three types of plugins:
1. **Metadata Enrichers** - Transform backend-specific data to canonical format
2. **Installers** - Handle mod installation workflows
3. **UI Extensions** - Add custom panels and views to the GUI

## Getting Started

### 1. Create a New Plugin Project

```bash
dotnet new classlib -n MyPlugin
cd MyPlugin
dotnet add reference path/to/Modular.Sdk/Modular.Sdk.csproj
```

### 2. Implement IPluginMetadata

Every plugin must have a class implementing `IPluginMetadata`:

```csharp
using Modular.Sdk;

public class MyPluginMetadata : IPluginMetadata
{
    public string Id => "my.plugin.id";
    public string DisplayName => "My Plugin";
    public string Version => "1.0.0";
    public string? Description => "What my plugin does";
    public string? Author => "Your Name";
}
```

### 3. Create a plugin.json Manifest

```json
{
  "id": "my.plugin.id",
  "display_name": "My Plugin",
  "version": "1.0.0",
  "author": "Your Name",
  "description": "What my plugin does",
  "entry_assembly": "MyPlugin.dll",
  "dependencies": [],
  "tags": ["category"]
}
```

## Plugin Types

### Metadata Enricher

Transforms backend-specific metadata into canonical format:

```csharp
using Modular.Sdk.Metadata;

public class MyEnricher : IMetadataEnricher
{
    public string BackendId => "mybackend";

    public async Task<object> EnrichAsync(
        object backendMetadata, 
        CancellationToken ct = default)
    {
        // Cast to your backend-specific type
        var sourceData = (MyBackendModInfo)backendMetadata;

        // Transform to canonical format
        return new CanonicalMod
        {
            CanonicalId = $"{BackendId}:{sourceData.Id}",
            Name = sourceData.Title,
            Summary = sourceData.Description,
            // ... map other fields
        };
    }
}
```

### Mod Installer

Handles mod installation with detection, analysis, and execution:

```csharp
using Modular.Sdk.Installers;

public class MyInstaller : IModInstaller
{
    public string InstallerId => "my-installer";
    public string DisplayName => "My Installer";
    public int Priority => 50; // Higher = preferred

    public async Task<InstallDetectionResult> DetectAsync(
        string archivePath, 
        CancellationToken ct = default)
    {
        // Check if this installer can handle the archive
        using var archive = ZipFile.OpenRead(archivePath);
        var hasMarkerFile = archive.Entries.Any(e => 
            e.Name == "my-installer.marker");

        return new InstallDetectionResult
        {
            CanHandle = hasMarkerFile,
            Confidence = hasMarkerFile ? 1.0 : 0.0,
            InstallerType = InstallerId,
            Reason = hasMarkerFile 
                ? "Contains marker file" 
                : "No marker file found"
        };
    }

    public async Task<InstallPlan> AnalyzeAsync(
        string archivePath,
        InstallContext context,
        CancellationToken ct = default)
    {
        // Create installation plan
        var plan = new InstallPlan
        {
            InstallerId = InstallerId,
            SourcePath = archivePath,
            Operations = new List<FileOperation>()
        };

        // Analyze archive and add file operations
        // ...

        return plan;
    }

    public async Task<InstallResult> InstallAsync(
        InstallPlan plan,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        // Execute installation
        var result = new InstallResult { Success = false };

        try
        {
            // Extract files, apply patches, etc.
            // Report progress via progress?.Report(...)
            
            result.Success = true;
            result.InstalledFiles = /* list of installed files */;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        return result;
    }
}
```

### UI Extension

Adds custom UI panels to the application:

```csharp
using Modular.Sdk.UI;

public class MyUiExtension : IUiExtension
{
    public string ExtensionId => "my.ui.extension";
    public string DisplayName => "My Panel";
    public UiExtensionLocation Location => UiExtensionLocation.MainTab;
    public int Priority => 50;
    public string? Icon => "🔧";

    public object CreateContent()
    {
        // Return Avalonia UserControl or ViewModel
        return new MyCustomViewModel();
    }

    public Task OnActivatedAsync(CancellationToken ct = default)
    {
        // Called when panel is shown
        return Task.CompletedTask;
    }

    public Task OnDeactivatedAsync(CancellationToken ct = default)
    {
        // Called when panel is hidden
        return Task.CompletedTask;
    }
}
```

## Building and Packaging

### 1. Build Your Plugin

```bash
dotnet build -c Release
```

### 2. Package for Distribution

Create a directory structure:
```
my-plugin/
├── plugin.json
├── MyPlugin.dll
└── dependencies/ (if any)
```

### 3. Distribute

Zip the directory or submit to the plugin marketplace.

## Installation

Users install plugins by:
1. Extracting to `~/.config/Modular/plugins/my-plugin/`
2. Restarting Modular
3. Or using the plugin marketplace UI

## Best Practices

### Error Handling
- Use try-catch blocks in all public methods
- Return meaningful error messages
- Don't throw exceptions from constructors

### Logging
- Plugins don't have direct access to loggers
- Return diagnostic information in result objects
- Use exception messages for error reporting

### Performance
- Cache expensive operations
- Use async/await properly
- Don't block the UI thread

### Compatibility
- Only reference Modular.Sdk, not Modular.Core
- Document minimum host version required
- Handle version mismatches gracefully

### Testing
- Test with minimal dependencies
- Provide sample data for testing
- Document expected behavior

## Example Plugin

See `examples/ExamplePlugin/` for a complete reference implementation.

## Troubleshooting

### Plugin Not Loading
- Check `plugin.json` is valid JSON
- Verify `entry_assembly` points to correct DLL
- Ensure DLL references only Modular.Sdk

### Components Not Discovered
- Verify class implements the correct interface
- Check class is public
- Ensure parameterless constructor exists

### Runtime Errors
- Check for missing dependencies
- Verify .NET version compatibility
- Review exception messages in logs

## Support

- File issues at: https://github.com/your-repo/issues
- Plugin development forum: (link)
- Example plugins: `examples/` directory
