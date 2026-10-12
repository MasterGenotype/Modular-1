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

/// <summary>
/// Message asking for files to be added to the download queue.
/// Lets a view that does not own the queue hand it work.
/// </summary>
public class EnqueueDownloadsMessage
{
    public EnqueueDownloadsMessage(
        IReadOnlyList<(Modular.Sdk.Backends.Common.BackendMod Mod, Modular.Sdk.Backends.Common.BackendModFile File)> items,
        bool addToInstaller = false)
    {
        Items = items;
        AddToInstaller = addToInstaller;
    }

    /// <summary>Whether the files go on the installer's list once they have downloaded.</summary>
    public bool AddToInstaller { get; }

    /// <summary>The files to queue, each with the mod it belongs to.</summary>
    public IReadOnlyList<(Modular.Sdk.Backends.Common.BackendMod Mod, Modular.Sdk.Backends.Common.BackendModFile File)> Items { get; }
}

/// <summary>
/// Message asking for downloaded archives to be handed to the mod installer.
/// Lets the download queue pass files to the Mod Manager without owning it.
/// </summary>
public class SendToInstallerMessage
{
    public SendToInstallerMessage(IReadOnlyList<string> archivePaths, bool navigate = true)
    {
        ArchivePaths = archivePaths;
        Navigate = navigate;
    }

    /// <summary>Whether to switch to the installer, or only add to its list.</summary>
    public bool Navigate { get; }

    /// <summary>Paths of the downloaded archives to install.</summary>
    public IReadOnlyList<string> ArchivePaths { get; }
}
