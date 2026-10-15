using Modular.Switch.Models;

namespace Modular.Switch.Totk;

/// <summary>
/// The Legend of Zelda: Tears of the Kingdom, the one Switch title installed by merging
/// (<see cref="TotkMergeService"/>) rather than by per-mod LayeredFS slots.
/// </summary>
public static class TotkTitle
{
    /// <summary>TotK's TitleID.</summary>
    public const string TitleIdValue = "0100F2C0115B6000";

    /// <summary>TotK's TitleID.</summary>
    public static SwitchTitleId TitleId { get; } = new(TitleIdValue);

    /// <summary>Name of the single mod folder the merged output is exposed as.</summary>
    public const string ModFolderName = "TKMM";

    /// <summary>True when <paramref name="titleId"/> is TotK and so installs by merging.</summary>
    public static bool Is(SwitchTitleId titleId) =>
        string.Equals(titleId.Value, TitleIdValue, StringComparison.OrdinalIgnoreCase);
}
