namespace Modular.Sdk.UI;

/// <summary>
/// Interface for UI extensions that plugins can provide.
/// Allows plugins to contribute custom panels, tabs, or views to the main application.
/// </summary>
public interface IUiExtension
{
    /// <summary>
    /// Unique identifier for this UI extension.
    /// </summary>
    string ExtensionId { get; }

    /// <summary>
    /// Display name for the UI extension.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Where this extension should appear in the UI.
    /// </summary>
    UiExtensionLocation Location { get; }

    /// <summary>
    /// Priority for ordering (higher = shown first).
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Icon identifier (optional).
    /// </summary>
    string? Icon { get; }

    /// <summary>
    /// Creates the view model or view content for this extension.
    /// </summary>
    /// <returns>UI content object (framework-specific, e.g. Avalonia Control or ViewModel).</returns>
    object CreateContent();

    /// <summary>
    /// Called when the extension is activated/shown.
    /// </summary>
    Task OnActivatedAsync(CancellationToken ct = default);

    /// <summary>
    /// Called when the extension is deactivated/hidden.
    /// </summary>
    Task OnDeactivatedAsync(CancellationToken ct = default);
}

/// <summary>
/// Location where a UI extension should be displayed.
/// </summary>
public enum UiExtensionLocation
{
    /// <summary>Main navigation tab.</summary>
    MainTab,

    /// <summary>Sidebar panel.</summary>
    Sidebar,

    /// <summary>Tools menu.</summary>
    ToolsMenu,

    /// <summary>Settings page section.</summary>
    Settings,

    /// <summary>Mod details page section.</summary>
    ModDetails,

    /// <summary>Status bar.</summary>
    StatusBar
}

/// <summary>
/// Event args for UI extension errors.
/// </summary>
public class UiExtensionErrorEventArgs : EventArgs
{
    /// <summary>Extension identifier.</summary>
    public string ExtensionId { get; set; } = string.Empty;

    /// <summary>Extension display name.</summary>
    public string ExtensionName { get; set; } = string.Empty;

    /// <summary>Exception that occurred.</summary>
    public Exception Exception { get; set; } = null!;

    /// <summary>Phase where error occurred (load, activate, deactivate).</summary>
    public string Phase { get; set; } = string.Empty;
}
