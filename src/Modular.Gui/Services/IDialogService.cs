namespace Modular.Gui.Services;

/// <summary>
/// Result of a dialog with multiple options.
/// </summary>
public enum DialogResult
{
    Ok,
    Cancel,
    Yes,
    No,
    Retry,
    Abort
}

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
    /// Shows an error dialog with a retry option.
    /// </summary>
    /// <returns>DialogResult indicating user's choice.</returns>
    Task<DialogResult> ShowRetryErrorAsync(string title, string message);

    /// <summary>
    /// Shows a progress dialog for long-running operations.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">Initial message.</param>
    /// <param name="cancellable">Whether the operation can be cancelled.</param>
    /// <returns>A progress reporter that can update the dialog.</returns>
    Task<IProgressDialog> ShowProgressAsync(string title, string message, bool cancellable = true);
}

/// <summary>
/// Interface for a progress dialog that can be updated.
/// </summary>
 public interface IProgressDialog : IDisposable
{
    /// <summary>
    /// Updates the progress value (0-100).
    /// </summary>
    void UpdateProgress(double progress);

    /// <summary>
    /// Updates the message displayed.
    /// </summary>
    void UpdateMessage(string message);

    /// <summary>
    /// Whether the user has requested cancellation.
    /// </summary>
    bool IsCancellationRequested { get; }

    /// <summary>
    /// Closes the dialog.
    /// </summary>
    void Close();
}
