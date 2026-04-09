using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Modular.Gui.Messages;

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
}

/// <summary>
/// Message sent when the download queue has been fully drained (all items processed).
/// Allows search ViewModels to clear their selection queues after downloads complete.
/// </summary>
public class DownloadBatchCompletedMessage
{
}
