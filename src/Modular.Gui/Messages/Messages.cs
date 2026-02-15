using CommunityToolkit.Mvvm.Messaging.Messages;
using Modular.Gui.Models;

namespace Modular.Gui.Messages;

/// <summary>
/// Message sent when a download completes.
/// Allows LibraryViewModel to refresh without direct coupling to DownloadQueueViewModel.
/// </summary>
public class DownloadCompletedMessage : ValueChangedMessage<DownloadCompletedInfo>
{
    public DownloadCompletedMessage(DownloadCompletedInfo value) : base(value) { }
}

/// <summary>
/// Information about a completed download.
/// </summary>
public class DownloadCompletedInfo
{
    public string DownloadId { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string OutputPath { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? GameDomain { get; init; }
    public string? ModId { get; init; }
}

/// <summary>
/// Message sent when settings change.
/// Allows backends and other services to react to configuration updates.
/// </summary>
public class SettingsChangedMessage : ValueChangedMessage<SettingsChangedInfo>
{
    public SettingsChangedMessage(SettingsChangedInfo value) : base(value) { }
}

/// <summary>
/// Information about a settings change.
/// </summary>
public class SettingsChangedInfo
{
    public string SettingName { get; init; } = string.Empty;
    public object? OldValue { get; init; }
    public object? NewValue { get; init; }
}

/// <summary>
/// Message sent when mods are selected for download.
/// Decouples ModListViewModel from DownloadQueueViewModel.
/// </summary>
public class ModsSelectedForDownloadMessage : ValueChangedMessage<ModsSelectedInfo>
{
    public ModsSelectedForDownloadMessage(ModsSelectedInfo value) : base(value) { }
}

/// <summary>
/// Information about selected mods for download.
/// </summary>
public class ModsSelectedInfo
{
    public IReadOnlyList<ModDisplayModel> SelectedMods { get; init; } = [];
    public string? GameDomain { get; init; }
    public string BackendId { get; init; } = string.Empty;
}

/// <summary>
/// Message sent when a backend operation starts or completes.
/// Allows UI to show/hide loading indicators.
/// </summary>
public class BackendOperationMessage : ValueChangedMessage<BackendOperationInfo>
{
    public BackendOperationMessage(BackendOperationInfo value) : base(value) { }
}

/// <summary>
/// Information about a backend operation.
/// </summary>
public class BackendOperationInfo
{
    public string OperationName { get; init; } = string.Empty;
    public string BackendId { get; init; } = string.Empty;
    public bool IsStarting { get; init; }
    public bool IsCompleted { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Message sent when the library needs to be refreshed.
/// </summary>
public class RefreshLibraryMessage
{
    public string? GameDomain { get; init; }
}

/// <summary>
/// Message sent when navigation is requested.
/// </summary>
public class NavigationRequestMessage : ValueChangedMessage<NavigationInfo>
{
    public NavigationRequestMessage(NavigationInfo value) : base(value) { }
}

/// <summary>
/// Information about a navigation request.
/// </summary>
public class NavigationInfo
{
    public string TargetView { get; init; } = string.Empty;
    public object? Parameter { get; init; }
}
