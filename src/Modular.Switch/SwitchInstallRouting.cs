using Modular.Switch.Models;
using Modular.Switch.Totk;

namespace Modular.Switch;

/// <summary>How mods for a Switch title are installed.</summary>
public enum SwitchInstallMethod
{
    /// <summary>
    /// Each mod is copied into its own LayeredFS slot under the emulator's load folder
    /// (<see cref="Installer.SwitchModInstaller"/>), and the emulator layers them.
    /// </summary>
    LayeredFsSlots,

    /// <summary>
    /// Tears of the Kingdom: mods are merged into one output folder
    /// (<see cref="TotkMergeService"/>), exposed to the emulator as one mod.
    /// </summary>
    TotkMerge
}

/// <summary>
/// Picks each Switch title's installer. Titles with an installer of their own are listed
/// here; every other title uses <see cref="SwitchInstallMethod.LayeredFsSlots"/>.
/// </summary>
public static class SwitchInstallRouting
{
    /// <summary>The install method for <paramref name="titleId"/>.</summary>
    public static SwitchInstallMethod For(SwitchTitleId titleId) =>
        TotkTitle.Is(titleId) ? SwitchInstallMethod.TotkMerge : SwitchInstallMethod.LayeredFsSlots;
}
