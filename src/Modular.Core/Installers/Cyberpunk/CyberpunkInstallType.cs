namespace Modular.Core.Installers.Cyberpunk;

/// <summary>
/// Represents the distinct installation types for Cyberpunk 2077 mods,
/// derived from archive content fingerprinting.
/// </summary>
[Flags]
public enum CyberpunkInstallType
{
    /// <summary>Archive contents are unrecognizable as a known Cyberpunk 2077 mod layout.</summary>
    Unknown = 0,

    /// <summary>
    /// RED4ext native plugin — DLL lives under red4ext/plugins/{name}/.
    /// Examples: ArchiveXL, TweakXL, Codeware, Input Loader.
    /// </summary>
    Red4ExtPlugin = 1 << 0,

    /// <summary>
    /// Cyber Engine Tweaks Lua mod — files live under bin/x64/plugins/cyber_engine_tweaks/mods/{name}/.
    /// Examples: Native Settings UI, Immersive First Person, ACU.
    /// </summary>
    CetMod = 1 << 1,

    /// <summary>
    /// redscript source mod — .reds files live under r6/scripts/{name}/.
    /// Examples: Mod Settings, Equipment-EX, Virtual Car Dealer.
    /// </summary>
    RedscriptMod = 1 << 2,

    /// <summary>
    /// Legacy archive — .archive (and optional .archive.xl) files live under archive/pc/mod/.
    /// Examples: No Intro Videos, Material and Texture Override (Legacy).
    /// </summary>
    LegacyArchive = 1 << 3,

    /// <summary>
    /// REDmod format — archive + info.json under mods/{ModName}/.
    /// Examples: Material and Texture Override (REDmod).
    /// </summary>
    RedMod = 1 << 4,

    /// <summary>
    /// TweakDB YAML/RED files live under r6/tweaks/{name}/.
    /// Examples: Virtual Car Dealer.
    /// </summary>
    TweakMod = 1 << 5,

    /// <summary>
    /// ini/config tweak — .ini placed into engine/config/platform/pc/.
    /// Examples: Better Vehicle Handling.
    /// </summary>
    IniTweak = 1 << 6,

    /// <summary>
    /// Framework/engine-level installation — drops files into bin/x64/ and/or engine/
    /// at the game root level. Typically loaders and compilers.
    /// Examples: Cyber Engine Tweaks, RED4ext, redscript, cybercmd.
    /// </summary>
    FrameworkRoot = 1 << 7,

    /// <summary>
    /// Standalone executable tool — not installed into the game directory at all.
    /// Examples: Save Editor (Project CyberCAT-SimpleGUI).
    /// </summary>
    StandaloneExe = 1 << 8,

    /// <summary>
    /// Input mapping XML files under r6/input/.
    /// Used alongside Input Loader.
    /// </summary>
    InputMapping = 1 << 9,

    /// <summary>
    /// ASI plugin — .asi file under bin/x64/plugins/.
    /// Examples: cybercmd.
    /// </summary>
    AsiPlugin = 1 << 10
}
