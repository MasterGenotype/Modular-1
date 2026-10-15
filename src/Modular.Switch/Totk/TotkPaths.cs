namespace Modular.Switch.Totk;

/// <summary>
/// Where the TotK merge installer keeps its own files. Everything lives under one root,
/// <c>~/.config/Modular/switch/totk</c> by default; tests pass their own.
/// </summary>
/// <param name="Root">The TotK root folder.</param>
public sealed record TotkPaths(string Root)
{
    /// <summary>The default root, <c>~/.config/Modular/switch/totk</c>.</summary>
    public static TotkPaths Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "Modular", "switch", "totk"));

    /// <summary>TkSharp's mod manager data: installed mods, profiles and changelogs.</summary>
    public string DataFolder => Path.Combine(Root, "data");

    /// <summary>The merge output used when no <c>merge_output</c> is configured.</summary>
    public string DefaultMergeOutput => Path.Combine(Root, "merged");

    /// <summary>The snapshot of the merge output taken before the latest merge.</summary>
    public string SnapshotFolder => Path.Combine(Root, "snapshots", "merge-output");
}
