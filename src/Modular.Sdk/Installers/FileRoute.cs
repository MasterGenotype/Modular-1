using Modular.Sdk.Modules;

namespace Modular.Sdk.Installers;

/// <summary>
/// Where one archive entry belongs, and which rule decided it.
/// </summary>
/// <remarks>
/// <para>
/// A destination alone is not enough to debug a mis-routed mod. The package type says what
/// kind of thing the file is, and the route source says whether the archive declared the
/// path, a manifest named it, or a loose-file rule guessed it — so a wrong destination
/// traces back to the rule responsible instead of having to be re-derived by reading the
/// analyzer.
/// </para>
/// <para>
/// Destinations are relative to <see cref="Root"/>, which is <see cref="ModRoots.Game"/>
/// unless the file belongs outside the game directory — user configuration under Documents,
/// for instance.
/// </para>
/// </remarks>
/// <param name="Destination">
/// Path relative to <paramref name="Root"/>, using forward slashes.
/// </param>
/// <param name="PackageType">
/// What kind of package this file belongs to, such as <c>cp77.legacy_archive</c>,
/// <c>ue.pak</c> or <c>hzd.patch_bin</c>. Null when the analyzer could not say.
/// </param>
/// <param name="RouteSource">Which rule decided the destination.</param>
/// <param name="Root">
/// Name of the deployment root the destination is relative to. Null means
/// <see cref="ModRoots.Game"/>.
/// </param>
public readonly record struct FileRoute(
    string Destination,
    string? PackageType = null,
    RouteSource RouteSource = RouteSource.Heuristic,
    string? Root = null)
{
    /// <summary>
    /// A route whose destination the archive already declared, so the tree deploys as-is.
    /// </summary>
    /// <param name="destination">Game-relative destination.</param>
    /// <param name="packageType">Package type id.</param>
    /// <returns>The route.</returns>
    public static FileRoute Declared(string destination, string? packageType = null) =>
        new(destination, packageType, RouteSource.Declared);

    /// <summary>
    /// A route an author- or loader-provided manifest decided.
    /// </summary>
    /// <param name="destination">Game-relative destination.</param>
    /// <param name="packageType">Package type id.</param>
    /// <returns>The route.</returns>
    public static FileRoute FromMetadata(string destination, string? packageType = null) =>
        new(destination, packageType, RouteSource.Metadata);

    /// <summary>
    /// A route a loose-file extension rule decided. The weakest signal.
    /// </summary>
    /// <param name="destination">Game-relative destination.</param>
    /// <param name="packageType">Package type id.</param>
    /// <returns>The route.</returns>
    public static FileRoute Heuristic(string destination, string? packageType = null) =>
        new(destination, packageType, RouteSource.Heuristic);

    /// <summary>
    /// The same route, pointed at a different deployment root.
    /// </summary>
    /// <param name="root">Root name, such as <see cref="ModRoots.Documents"/>.</param>
    /// <returns>The route, rooted.</returns>
    public FileRoute InRoot(string root) => this with { Root = root };

    /// <inheritdoc />
    public override string ToString() =>
        $"{Root ?? ModRoots.Game}/{Destination} [{PackageType ?? "unknown"}, {RouteSource}]";
}

/// <summary>
/// Package type ids the built-in analyzers label their routes with.
/// </summary>
/// <remarks>
/// Ids are namespaced by game or engine family, because a destination is a property of the
/// package type as much as of the game: one game routinely needs several destinations, and
/// the same package type can appear across games (a UE pak, for instance).
/// </remarks>
public static class PackageTypes
{
    /// <summary>A Cyberpunk legacy <c>.archive</c> or <c>.archive.xl</c>.</summary>
    public const string Cp77LegacyArchive = "cp77.legacy_archive";

    /// <summary>A Cyberpunk REDmod folder, identified by its <c>info.json</c>.</summary>
    public const string Cp77RedMod = "cp77.redmod";

    /// <summary>A RED4ext plugin.</summary>
    public const string Cp77Red4ExtPlugin = "cp77.red4ext_plugin";

    /// <summary>A Cyber Engine Tweaks mod folder, identified by its <c>init.lua</c>.</summary>
    public const string Cp77CetMod = "cp77.cet_mod";

    /// <summary>A redscript source file.</summary>
    public const string Cp77Redscript = "cp77.redscript";

    /// <summary>A TweakXL tweak.</summary>
    public const string Cp77Tweak = "cp77.tweak";

    /// <summary>An input-mapping XML.</summary>
    public const string Cp77InputMapping = "cp77.input_mapping";

    /// <summary>An engine or user configuration file.</summary>
    public const string Cp77IniTweak = "cp77.ini_tweak";

    /// <summary>An ASI plugin.</summary>
    public const string Cp77AsiPlugin = "cp77.asi_plugin";

    /// <summary>A loader framework (RED4ext, CET, redscript, ArchiveXL, TweakXL).</summary>
    public const string Cp77Framework = "cp77.framework";

    /// <summary>A single Unreal <c>.pak</c>.</summary>
    public const string UePak = "ue.pak";

    /// <summary>A <c>.pak</c> + <c>.utoc</c> + <c>.ucas</c> IoStore trio.</summary>
    public const string UePakBundle = "ue.pak_bundle";

    /// <summary>A proxy DLL or ASI hook next to the game executable.</summary>
    public const string UeHook = "ue.hook";

    /// <summary>A 3DMigoto framework or shader package.</summary>
    public const string UeMigoto = "ue.migoto";

    /// <summary>A DXVK translation layer.</summary>
    public const string Dxvk = "dxvk.wrapper";

    /// <summary>A user configuration INI, which UE reads from Documents rather than the game.</summary>
    public const string UeUserConfig = "ue.user_config";

    /// <summary>A Decima <c>Patch_*.bin</c>.</summary>
    public const string HzdPatchBin = "hzd.patch_bin";

    /// <summary>A Horizon Zero Dawn loose asset or loader file.</summary>
    public const string HzdLooseFile = "hzd.loose_file";

    /// <summary>A Baldur's Gate 3 <c>.pak</c> mod, which lives in the user <c>Mods</c> folder.</summary>
    public const string Bg3Pak = "bg3.pak";

    /// <summary>A Baldur's Gate 3 loose override file beneath the install's <c>Data</c> folder.</summary>
    public const string Bg3LooseData = "bg3.loose_data";

    /// <summary>A Baldur's Gate 3 native loader file beside the executable (Script Extender, NativeMods).</summary>
    public const string Bg3Native = "bg3.native";

    /// <summary>
    /// Baldur's Gate 3 Script Extender: <c>DWrite.dll</c> and its settings, which go beside
    /// the game executable in <c>bin/</c>.
    /// </summary>
    public const string Bg3ScriptExtender = "bg3.script_extender";

    /// <summary>Anything the analyzer could not classify.</summary>
    public const string Unknown = "unknown";
}
