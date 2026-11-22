using System.Diagnostics;

namespace Modular.Gui.Services;

/// <summary>Opens a folder in the desktop's file manager, as the Library's "Open folder" does.</summary>
public static class FolderOpener
{
    public static bool Open(string folder)
    {
        if (!Directory.Exists(folder))
            return false;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
