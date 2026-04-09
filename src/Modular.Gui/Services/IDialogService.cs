namespace Modular.Gui.Services;

/// <summary>
/// Service for showing dialogs to the user.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Shows an error dialog.
    /// </summary>
    Task ShowErrorAsync(string title, string message);

    /// <summary>
    /// Shows a warning dialog.
    /// </summary>
    Task ShowWarningAsync(string title, string message);

    /// <summary>
    /// Shows a confirmation dialog.
    /// </summary>
    /// <returns>True if the user confirmed, false otherwise.</returns>
    Task<bool> ShowConfirmationAsync(string title, string message);

    /// <summary>
    /// Shows an input dialog.
    /// </summary>
    /// <returns>The user's input, or null if cancelled.</returns>
    Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null);

    /// <summary>
    /// Shows a folder browser dialog.
    /// </summary>
    /// <returns>The selected folder path, or null if cancelled.</returns>
    Task<string?> ShowFolderBrowserAsync(string? title = null, string? initialDirectory = null);

    /// <summary>
    /// Shows a file browser dialog.
    /// </summary>
    /// <returns>List of selected file paths, or empty if cancelled.</returns>
    Task<List<string>> ShowFileBrowserAsync(string? title = null, bool allowMultiple = false, string? initialDirectory = null);

    /// <summary>
    /// Shows a list picker dialog.
    /// </summary>
    /// <returns>The selected index, or -1 if cancelled.</returns>
    Task<int> ShowListPickerAsync(string title, string message, List<string> items);

    /// <summary>
    /// Shows a multi-select dialog with checkboxes.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">Description message.</param>
    /// <param name="items">Items to display with labels.</param>
    /// <param name="preSelected">Indices of items that should be pre-selected. Null selects all.</param>
    /// <returns>List of selected indices, or empty if cancelled.</returns>
    Task<List<int>> ShowMultiSelectAsync(string title, string message, List<string> items, List<int>? preSelected = null);
}
