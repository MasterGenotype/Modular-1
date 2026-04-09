using System.Collections.ObjectModel;

namespace Modular.Gui.ViewModels;

/// <summary>
/// Represents a file or directory node in the mod file tree.
/// </summary>
public class FileTreeItem
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public ObservableCollection<FileTreeItem> Children { get; set; } = new();
    public int Depth { get; set; }

    public bool IsFile => !IsDirectory;
}
