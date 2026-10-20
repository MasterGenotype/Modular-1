using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Modular.Gui.ViewModels;

/// <summary>
/// What a node of the library tree stands for.
/// </summary>
public enum LibraryNodeKind
{
    /// <summary>A backend folder: <c>nexusmods/</c>, <c>gamebanana/</c> or <c>modio/</c>.</summary>
    Backend,

    /// <summary>A game folder within a backend folder.</summary>
    Game,

    /// <summary>A mod folder within a game folder.</summary>
    Mod
}

/// <summary>
/// A folder in the library tree, which follows the mods directory:
/// backend, then game, then mod.
/// </summary>
public partial class LibraryNode : ObservableObject
{
    /// <summary>The folder's name.</summary>
    public required string Name { get; init; }

    /// <summary>The line under the name.</summary>
    public string Caption { get; init; } = string.Empty;

    /// <summary>What the folder is.</summary>
    public LibraryNodeKind Kind { get; init; }

    /// <summary>The game, for a game node and for a mod node's game.</summary>
    public GameDomainItem? Game { get; init; }

    /// <summary>The mod, for a mod node.</summary>
    public ModFolderItem? Mod { get; init; }

    /// <summary>The node holding this one; null for a backend.</summary>
    public LibraryNode? Parent { get; init; }

    /// <summary>The folders inside.</summary>
    public ObservableCollection<LibraryNode> Children { get; } = new();

    /// <summary>Whether the node is open in the tree.</summary>
    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Whether this is a backend folder.</summary>
    public bool IsBackend => Kind == LibraryNodeKind.Backend;

    /// <summary>Whether this is a game folder.</summary>
    public bool IsGame => Kind == LibraryNodeKind.Game;

    /// <summary>Whether this is a mod folder.</summary>
    public bool IsMod => Kind == LibraryNodeKind.Mod;
}
