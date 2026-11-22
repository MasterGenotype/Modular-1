using Modular.Sdk.Installers;

namespace Modular.Gui.Services;

/// <summary>
/// Asks the user each installer question the plan left open (FOMOD pages, variant folders, …),
/// one dialog per question. Used by installs, updates and mod-list imports alike; questions
/// answered from a mod's saved answers never reach it.
/// </summary>
public static class InstallChoiceDialog
{
    public static Func<IReadOnlyList<InstallChoice>, CancellationToken, Task<IReadOnlyDictionary<string, string>?>> For(
        IDialogService dialogs, string? modName = null) =>
        async (choices, ct) =>
        {
            var answers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var choice in choices.Where(c => !c.IsResolved))
            {
                ct.ThrowIfCancellationRequested();
                // The install runs off the UI thread once it starts doing I/O.
                var index = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    dialogs.ShowListPickerAsync(
                        modName is null ? "Choose an Option" : $"Choose an Option · {modName}",
                        choice.Title,
                        choice.Options.Select(o => $"{o.Label} ({o.FileCount} file(s))").ToList()));
                if (index < 0 || index >= choice.Options.Count)
                    return null;
                answers[choice.Id] = choice.Options[index].Id;
            }
            return answers;
        };
}
