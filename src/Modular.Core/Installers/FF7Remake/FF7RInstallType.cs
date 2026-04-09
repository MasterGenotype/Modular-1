namespace Modular.Core.Installers.FF7Remake;

/// <summary>
/// Represents the distinct installation types for Final Fantasy VII Remake
/// Intergrade mods, derived from archive content fingerprinting.
///
/// FF7R is an Unreal Engine 4 title. Most mods are .pak files destined for
/// <c>End\Content\Paks\~mods\</c>, but the ecosystem also includes DLL hooks,
/// 3DMigoto shader mods, DXVK wrappers, and INI tweaks —
/// each targeting a different subdirectory of the game root.
/// </summary>
[Flags]
public enum FF7RInstallType
{
    /// <summary>Archive contents are unrecognizable as a known FF7R mod layout.</summary>
    Unknown = 0,

    /// <summary>
    /// UE4 .pak file(s) destined for End\Content\Paks\~mods\.
    /// By far the most common type — covers outfits, textures, gameplay tweaks,
    /// model swaps, weapon mods, and data table overrides.
    /// Examples: Cloud Advent Children Outfit, Tifa Invisible Weapons, Lv.99 Mod.
    /// </summary>
    PakMod = 1 << 0,

    /// <summary>
    /// DLL/ASI hook injected into End\Binaries\Win64\ alongside ff7remake_.exe.
    /// Typically a proxy DLL (xinput1_3.dll, dxgi.dll, dinput8.dll) that intercepts
    /// engine calls to enable console, unlock INI settings, etc.
    /// Examples: FFVIIHook.
    /// </summary>
    DllHook = 1 << 1,

    /// <summary>
    /// 3DMigoto framework installation: d3dx.ini, dxgi.dll (3DMigoto loader),
    /// ShaderFixes/, and a Mods/ subfolder — all under End\Binaries\Win64\.
    /// Requires the game to run in DX11 mode.
    /// Example: 3DMigoto Base Mod for FFVII Remake.
    /// </summary>
    ThreeDMigoto = 1 << 2,

    /// <summary>
    /// 3DMigoto-dependent shader/texture mod placed into End\Binaries\Win64\Mods\.
    /// Requires the 3DMigoto base mod to be installed first.
    /// </summary>
    ThreeDMigotoMod = 1 << 3,

    /// <summary>
    /// DXVK DLL wrapper (d3d11.dll + dxgi.dll from DXVK) placed into
    /// End\Binaries\Win64\ for DirectX-to-Vulkan translation.
    /// Identified by DXVK-specific markers (dxvk.conf, DXVK version strings).
    /// Example: Stuttering fix - DXVK method.
    /// </summary>
    DxvkWrapper = 1 << 4,

    /// <summary>
    /// Engine.ini / Input.ini config overrides placed into the user's
    /// Documents\My Games\FINAL FANTASY VII REMAKE\Saved\Config\WindowsNoEditor\ path,
    /// or into engine/config directories relative to game root.
    /// Example: FFVIIHook's bundled Engine.ini.
    /// </summary>
    IniConfig = 1 << 5,

    /// <summary>
    /// UE4 .pak file placed directly in End\Content\Paks\ (NOT ~mods).
    /// Some older or experimental mods use this path instead of ~mods.
    /// </summary>
    PakRoot = 1 << 6
}
