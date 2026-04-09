namespace Modular.Core.Installers.HorizonZeroDawn;

/// <summary>
/// Represents the distinct installation types for Horizon Zero Dawn mods,
/// derived from archive content fingerprinting.
///
/// HZD uses the Decima engine. Most mods are Patch_*.bin files placed in
/// <c>Packed_DX12/</c>. The modding ecosystem has no dedicated framework
/// (no SKSE, CET, or RED4ext equivalent) — mods work by overriding packed
/// data via .bin patch files loaded alphabetically by the engine.
/// </summary>
[Flags]
public enum HZDInstallType
{
    /// <summary>Archive contents are unrecognizable as a known HZD mod layout.</summary>
    Unknown = 0,

    /// <summary>
    /// Decima patch .bin file(s) destined for Packed_DX12/.
    /// By far the most common type — covers textures, model replacements,
    /// outfit swaps, gameplay tweaks, face reworks, hair colors, etc.
    /// Files are typically named Patch_*.bin.
    /// </summary>
    DecimaPatch = 1 << 0,

    /// <summary>
    /// DLL hook/proxy injected into the game root folder next to
    /// HorizonZeroDawn.exe. Typically winhttp.dll as a proxy.
    /// Example: Gameplay Tweaks and Cheat Menu.
    /// </summary>
    DllHook = 1 << 1,

    /// <summary>
    /// ReShade preset — ReShade DLLs, shaders, and .ini preset files
    /// placed in the game root (next to HorizonZeroDawn.exe).
    /// Examples: Natural Clover ReShade, Photorealistic Reshade, True HDR.
    /// </summary>
    ReShadePreset = 1 << 2,

    /// <summary>
    /// Cheat Engine table (.CT file). Not installed into the game directory
    /// at all — loaded externally by Cheat Engine.
    /// </summary>
    CheatTable = 1 << 3,

    /// <summary>
    /// Save file placed into Documents\Horizon Zero Dawn\Saved Game\.
    /// Example: New Game Plus Save.
    /// </summary>
    SaveFile = 1 << 4,

    /// <summary>
    /// DLL replacement + Windows registry patch for GPU utility mods
    /// (FSR2, DLSS unlocking). Files go to game root alongside .reg files.
    /// Examples: DLSS Unlocker, FSR2HZD.
    /// </summary>
    GpuUtility = 1 << 5,

    /// <summary>
    /// EXE or core file replacement in the game root.
    /// Example: Run game on Phenom/Core2 (replaces game binaries).
    /// </summary>
    BinaryReplacement = 1 << 6,

    /// <summary>
    /// TOML/INI configuration file accompanying a DLL hook.
    /// Example: mod_config.toml for Gameplay Tweaks.
    /// </summary>
    ConfigFile = 1 << 7
}
