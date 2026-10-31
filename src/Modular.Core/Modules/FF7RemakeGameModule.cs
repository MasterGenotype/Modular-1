using Modular.Core.Archives;
using Modular.Core.Installers.FF7Remake;
using Modular.Sdk;
using Modular.Sdk.Archives;
using Modular.Sdk.Installers;
using Modular.Sdk.Modules;

namespace Modular.Core.Modules;

/// <summary>
/// Game module for Final Fantasy VII Remake Intergrade (Steam AppID 1462040).
/// </summary>
/// <remarks>
/// <para>
/// Nearly every mod is a <c>_P.pak</c> in <c>End/Content/Paks/~mods/</c>. Hooks and 3DMigoto
/// go to <c>End/Binaries/Win64/</c>, and user INIs belong in Documents rather than the game
/// folder — UE4 never reads user configuration from the install directory.
/// </para>
/// <para>
/// FF7 Rebirth (AppID 2909400) shares the same <c>End/</c> project layout but a completely
/// different mod ecosystem, so its executable is a rejecting anchor.
/// </para>
/// </remarks>
public sealed class FF7RemakeGameModule : GameModuleBase
{
    private readonly IArchiveReaderFactory _archiveReaderFactory;

    /// <summary>
    /// Path of the FF7 Remake user config directory, relative to Documents.
    /// </summary>
    /// <remarks>
    /// UE4 reads user configuration from here, not from a <c>Config/</c> folder in the game
    /// directory, so INI mods routed into the game folder silently do nothing.
    /// </remarks>
    public const string UserConfigRelativeToDocuments =
        "My Games/FINAL FANTASY VII REMAKE/Saved/Config/WindowsNoEditor";

    /// <summary>Name of the module-specific root holding user configuration.</summary>
    public const string UserConfigRoot = "userconfig";

    /// <summary>The pak folder, relative to the game root.</summary>
    public const string PaksModsFolder = "End/Content/Paks/~mods";

    /// <summary>
    /// Paks that must keep their own name and load after every order-prefixed pak.
    /// </summary>
    /// <remarks>
    /// Loader paks rely on their own filenames to sort last — Rebirth's
    /// <c>ZGameInstanceLoader.pak</c> is the reference case. Prefixing one would drag it into
    /// the ordered band and could break the loader. The FF7 Remake list is empty because no
    /// such loader is known for it; the mechanism is generic, and an unprefixed <c>Z*</c> pak
    /// raises a warning so the user can ask for it to be pinned.
    /// </remarks>
    public static IReadOnlyList<string> PinnedLastPaks { get; } = Array.Empty<string>();

    /// <summary>Creates the module.</summary>
    /// <param name="archiveReaderFactory">Shared archive reader factory.</param>
    public FF7RemakeGameModule(IArchiveReaderFactory? archiveReaderFactory = null)
    {
        _archiveReaderFactory = archiveReaderFactory ?? new ArchiveReaderFactory();
    }

    /// <inheritdoc />
    public override string ModuleId => "ff7remake";

    /// <inheritdoc />
    public override string DisplayName => "Final Fantasy VII Remake Intergrade";

    /// <inheritdoc />
    public override IReadOnlyList<string> GameIds { get; } =
        [Sdk.GameIds.FinalFantasy7Remake, "1462040"];

    /// <inheritdoc />
    public override IReadOnlyList<int> SteamAppIds { get; } = [1462040];

    /// <inheritdoc />
    protected override IReadOnlyList<string> PrimaryAnchors { get; } =
        ["End/Binaries/Win64/ff7remake_.exe", "End/Content/Paks"];

    /// <inheritdoc />
    protected override IReadOnlyList<string> SupportingAnchors { get; } =
        ["End/Content/Paks/~mods"];

    /// <summary>
    /// FF7 Rebirth's executable. Rebirth shares the <c>End/</c> layout, so its presence
    /// proves this directory is not Remake.
    /// </summary>
    protected override IReadOnlyList<string> RejectingAnchors { get; } =
        ["End/Binaries/Win64/ff7rebirth_.exe"];

    /// <inheritdoc />
    protected override IReadOnlyDictionary<string, string>? GetExtraRoots(
        GameInstallation installation)
    {
        var documents = ModRoots.ResolveStandardRoot(installation, ModRoots.Documents);
        if (string.IsNullOrEmpty(documents))
            return null;

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [UserConfigRoot] = Path.Combine(
                documents,
                UserConfigRelativeToDocuments.Replace('/', Path.DirectorySeparatorChar))
        };
    }

    /// <inheritdoc />
    public override IReadOnlyList<IModInstaller> GetInstallers() =>
        [new FF7RModInstaller(_archiveReaderFactory)];

    /// <summary>
    /// Routes user config to Documents and encodes load order into pak filenames.
    /// </summary>
    /// <param name="context">The staged file plus its ordering information.</param>
    /// <returns>Where the file should actually be deployed.</returns>
    /// <remarks>
    /// <para>
    /// UE mounts paks in alphabetical order and the last mount wins, which is exactly
    /// Modular's rule — so a zero-padded order prefix on the filename makes the engine agree
    /// with the user's list. The <c>_P</c> suffix is preserved because that is what gives a
    /// pak patch priority, and <c>.utoc</c>/<c>.ucas</c>/<c>.sig</c> siblings are renamed with
    /// their pak because the three must share a stem.
    /// </para>
    /// <para>
    /// A pinned pak is exempt: it keeps its name and the planner places it after the numbered
    /// band.
    /// </para>
    /// </remarks>
    public override DeployTarget MapDeployPath(DeployPathContext context)
    {
        // An INI the analyzer could only label as user config belongs under Documents.
        if (string.Equals(context.PackageType, PackageTypes.UeUserConfig, StringComparison.Ordinal))
        {
            return new DeployTarget(
                UserConfigRoot, Path.GetFileName(context.RelativePath));
        }

        // A route the installer already sent to a non-game root is left alone.
        if (!string.Equals(context.RootName, ModRoots.Game, StringComparison.OrdinalIgnoreCase))
            return new DeployTarget(context.RootName, context.RelativePath);

        var relativePath = context.RelativePath.Replace('\\', '/');
        var folder = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? string.Empty;

        if (!string.Equals(folder, PaksModsFolder, StringComparison.OrdinalIgnoreCase))
            return new DeployTarget(context.RootName, relativePath);

        var fileName = Path.GetFileName(relativePath);

        if (!IsOrderablePakFile(fileName))
            return new DeployTarget(context.RootName, relativePath);

        if (IsPinnedLast(fileName))
            return new DeployTarget(context.RootName, relativePath, PinnedLast: true);

        // Pad to the width the whole list needs, so filenames sort the same way numbers do.
        var width = Math.Max(3, context.EnabledModCount.ToString().Length);
        var prefix = context.LoadPosition.ToString(
            new string('0', width), System.Globalization.CultureInfo.InvariantCulture);

        return new DeployTarget(context.RootName, $"{folder}/{prefix}_{fileName}");
    }

    /// <summary>
    /// Modular keeps FF7 Remake's load order and writes it into the <c>~mods</c> pak
    /// prefixes; the install order still decides loose files such as hooks and INIs.
    /// </summary>
    public override LoadOrderKind LoadOrderKind => LoadOrderKind.Managed;

    /// <inheritdoc />
    public override EngineOrderDescription? DescribeEngineOrder(
        GameInstallation installation,
        IReadOnlyList<DeployedMod> mods)
    {
        var paks = mods
            .OrderBy(m => m.LoadPosition)
            .SelectMany(m => m.DeployedFiles)
            .Where(f =>
                string.Equals(f.RootName, ModRoots.Game, StringComparison.OrdinalIgnoreCase) &&
                f.RelativePath.StartsWith(PaksModsFolder, StringComparison.OrdinalIgnoreCase) &&
                f.RelativePath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetFileName(f.RelativePath))
            // Alphabetical is the engine's own mount order.
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        if (paks.Count == 0)
            return null;

        return new EngineOrderDescription(
            "alphabetical mount order within ~mods; the last mounted pak wins",
            paks,
            "filename prefixes");
    }

    /// <summary>
    /// Checks a staged mod against FF7 Remake's pak rules.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <param name="stagedMod">The staged mod.</param>
    /// <returns>Diagnostics; all warnings, because each case can still work.</returns>
    public override IReadOnlyList<ModDiagnostic> ValidateStaged(
        GameInstallation installation,
        StagedMod stagedMod)
    {
        ArgumentNullException.ThrowIfNull(stagedMod);

        var diagnostics = new List<ModDiagnostic>();

        var gameFiles = stagedMod.Files
            .Where(f => string.Equals(f.RootName, ModRoots.Game, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.RelativePath.Replace('\\', '/'))
            .ToList();

        foreach (var path in gameFiles.Where(p =>
                     p.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)))
        {
            var fileName = Path.GetFileName(path);
            var folder = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? string.Empty;

            if (!string.Equals(folder, PaksModsFolder, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(ModDiagnostic.Warning(
                    "ff7r.pak_outside_mods",
                    $"'{path}' is a pak outside {PaksModsFolder}/. UE mounts ~mods last, so a " +
                    "pak elsewhere may be overridden by the base game or not load at all.",
                    path));
            }

            if (!Path.GetFileNameWithoutExtension(fileName)
                    .EndsWith("_P", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(ModDiagnostic.Warning(
                    "ff7r.pak_missing_p_suffix",
                    $"'{fileName}' does not end in _P. Only a _P pak gets patch priority, so " +
                    "this one may lose to the base game's assets.",
                    path));
            }

            // A pak whose name starts with Z or zz was named that way to sort last. If it is
            // not pinned, order-prefixing will move it into the numbered band.
            if (LooksSelfSortingLast(fileName) && !IsPinnedLast(fileName))
            {
                diagnostics.Add(ModDiagnostic.Warning(
                    "ff7r.pak_self_sorting_last",
                    $"'{fileName}' is named to sort last, but it is not in the pinned list, so " +
                    "Modular will order-prefix it and it will no longer load last.",
                    path));
            }
        }

        if (gameFiles.Any(p => p.Contains("3dmigoto", StringComparison.OrdinalIgnoreCase)) ||
            gameFiles.Any(p => Path.GetFileName(p)
                .Equals("d3d11.dll", StringComparison.OrdinalIgnoreCase)))
        {
            diagnostics.Add(ModDiagnostic.Warning(
                "ff7r.requires_dx11",
                "3DMigoto and DXVK only work in DirectX 11 mode; launch the game with DX11 " +
                "or this mod will have no effect."));
        }

        return diagnostics;
    }

    /// <inheritdoc />
    public override LaunchRequirements GetLaunchRequirements(
        GameInstallation installation,
        IReadOnlyList<DeployedMod> mods)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(mods);

        if (!installation.IsProton)
            return LaunchRequirements.None;

        // A hook or wrapper DLL beside the executable is ignored under Proton unless it is
        // overridden native-first.
        var hookNames = new[] { "dxgi", "xinput1_3", "dinput8", "d3d11", "winmm", "version" };

        var overrides = mods
            .SelectMany(m => m.DeployedFiles)
            .Where(f =>
                string.Equals(f.RootName, ModRoots.Game, StringComparison.OrdinalIgnoreCase) &&
                f.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetFileNameWithoutExtension(f.RelativePath).ToLowerInvariant())
            .Where(name => hookNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        return overrides.Count == 0
            ? LaunchRequirements.None
            : new LaunchRequirements { WineDllOverrides = overrides };
    }

    /// <summary>
    /// Whether a pak filename should take an order prefix.
    /// </summary>
    /// <param name="fileName">Filename, no directory.</param>
    /// <returns>True for a pak or one of its IoStore siblings.</returns>
    private static bool IsOrderablePakFile(string fileName) =>
        fileName.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".ucas", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".sig", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a pak is declared to load after every order-prefixed pak.
    /// </summary>
    /// <param name="fileName">Filename, no directory.</param>
    /// <returns>True when a pinned-last glob matches.</returns>
    private static bool IsPinnedLast(string fileName) =>
        PinnedLastPaks.Any(glob =>
            Deployment.DeploymentPlanner.MatchesGlob(fileName, glob));

    /// <summary>
    /// Whether a filename was chosen so the engine sorts it last.
    /// </summary>
    /// <param name="fileName">Filename, no directory.</param>
    /// <returns>True for the <c>Z…</c> / <c>zz…</c> convention.</returns>
    private static bool LooksSelfSortingLast(string fileName) =>
        fileName.StartsWith("zz", StringComparison.OrdinalIgnoreCase) ||
        (fileName.Length > 1 && char.ToLowerInvariant(fileName[0]) == 'z' &&
         char.IsUpper(fileName[0]));
}
