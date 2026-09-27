using Modular.Core.Archives;
using Modular.Core.Installers.Cyberpunk;
using Modular.Sdk;
using Modular.Sdk.Archives;
using Modular.Sdk.Installers;
using Modular.Sdk.Modules;

namespace Modular.Core.Modules;

/// <summary>
/// Game module for Cyberpunk 2077 (Steam AppID 1091500).
/// </summary>
/// <remarks>
/// <para>
/// Mods are merged into the game root: <c>archive/</c>, <c>bin/</c>, <c>r6/</c>,
/// <c>red4ext/</c>, <c>engine/</c> and <c>mods/</c>.
/// </para>
/// <para>
/// Legacy <c>.archive</c> files load in ASCII order and the <em>first</em> one loaded wins,
/// which is the opposite of Modular's "later wins" rule. The translation happens in
/// <see cref="WriteLoadOrderAsync"/>, which writes <c>modlist.txt</c> in reverse.
/// </para>
/// </remarks>
public sealed class CyberpunkGameModule : GameModuleBase
{
    private readonly IArchiveReaderFactory _archiveReaderFactory;

    /// <summary>Path of the legacy archive folder, relative to the game root.</summary>
    public const string LegacyArchiveFolder = "archive/pc/mod";

    /// <summary>Path of the load-order manifest, relative to the game root.</summary>
    public const string ModListPath = "archive/pc/mod/modlist.txt";

    /// <summary>Launch argument Cyberpunk needs before it will load any REDmod.</summary>
    public const string ModdedLaunchArgument = "-modded";

    /// <summary>Creates the module.</summary>
    /// <param name="archiveReaderFactory">Shared archive reader factory.</param>
    public CyberpunkGameModule(IArchiveReaderFactory? archiveReaderFactory = null)
    {
        _archiveReaderFactory = archiveReaderFactory ?? new ArchiveReaderFactory();
    }

    /// <inheritdoc />
    public override string ModuleId => "cyberpunk2077";

    /// <inheritdoc />
    public override string DisplayName => "Cyberpunk 2077";

    /// <inheritdoc />
    public override IReadOnlyList<string> GameIds { get; } =
        [Sdk.GameIds.Cyberpunk2077, "1091500"];

    /// <inheritdoc />
    public override IReadOnlyList<int> SteamAppIds { get; } = [1091500];

    /// <inheritdoc />
    protected override IReadOnlyList<string> PrimaryAnchors { get; } =
        ["bin/x64/Cyberpunk2077.exe", "archive/pc/content"];

    /// <inheritdoc />
    protected override IReadOnlyList<string> SupportingAnchors { get; } =
        ["r6", "engine", "archive/pc/ep1"];

    /// <inheritdoc />
    public override IReadOnlyList<IModInstaller> GetInstallers() =>
        [new CyberpunkModInstaller(_archiveReaderFactory)];

    /// <summary>
    /// Files the game and its tools write after deployment.
    /// </summary>
    /// <returns>Root-relative globs that purge and conflict reporting must ignore.</returns>
    /// <remarks>
    /// <c>r6/cache</c> is rebuilt by redscript at every launch (it keeps the vanilla copy
    /// as <c>final.redscripts.bk</c>), <c>r6/cache/modded</c> is REDmod deploy output, and
    /// each CET mod's <c>db.sqlite3</c> is the user's own state. Purging any of them would
    /// destroy state or clobber a tool's output.
    /// </remarks>
    public override IReadOnlyList<string> GetGeneratedPaths() =>
    [
        "r6/cache/**",
        "bin/x64/plugins/cyber_engine_tweaks/mods/*/db.sqlite3",
        "bin/x64/plugins/cyber_engine_tweaks/mods/*/*.log",
        "**/*.log"
    ];

    /// <summary>
    /// Writes <c>archive/pc/mod/modlist.txt</c> in the order the engine wants it.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <param name="mods">Deployed mods in Modular order, where later wins.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the manifest has been written.</returns>
    /// <remarks>
    /// <para>
    /// The list is written highest-priority first, which is Modular's order <em>reversed</em>:
    /// for legacy archives the first entry loaded wins, so the mod the user put last has to
    /// appear first.
    /// </para>
    /// <para>
    /// Archives Modular does not manage are appended after the managed ones in ASCII order,
    /// because a partial <c>modlist.txt</c> has ambiguous semantics — anything omitted would
    /// be at the mercy of the engine's default ordering relative to the listed entries.
    /// </para>
    /// </remarks>
    public override async Task WriteLoadOrderAsync(
        GameInstallation installation,
        IReadOnlyList<DeployedMod> mods,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(mods);

        var archiveDir = Path.Combine(
            installation.InstallPath,
            LegacyArchiveFolder.Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(archiveDir))
            return;

        var managed = ManagedArchiveNames(mods);

        var unmanaged = Directory
            .EnumerateFiles(archiveDir, "*.archive")
            .Select(Path.GetFileName)
            .Where(name => name != null && !managed.Contains(name!, StringComparer.OrdinalIgnoreCase))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var manifestPath = Path.Combine(archiveDir, "modlist.txt");
        var lines = new List<string>
        {
            "# Written by Modular. Highest priority first: for legacy archives the first",
            "# entry loaded wins, so this is Modular's load order reversed.",
        };

        lines.AddRange(managed);
        lines.AddRange(unmanaged);

        await File.WriteAllLinesAsync(manifestPath, lines, ct);
    }

    /// <inheritdoc />
    public override EngineOrderDescription? DescribeEngineOrder(
        GameInstallation installation,
        IReadOnlyList<DeployedMod> mods)
    {
        var managed = ManagedArchiveNames(mods);

        if (managed.Count == 0)
            return null;

        return new EngineOrderDescription(
            "ASCII order unless modlist.txt overrides it; the first archive loaded wins",
            managed,
            ModListPath);
    }

    /// <summary>
    /// Reports what the user must do for the deployed set to load.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <param name="mods">The deployed mods.</param>
    /// <returns>Launch arguments, Wine overrides and the REDmod deploy command.</returns>
    /// <remarks>
    /// <para>
    /// REDmods do not load at all without <c>-modded</c>, and they are only visible to the
    /// game after <c>redMod.exe deploy</c> has run — so both are reported whenever a REDmod
    /// is deployed.
    /// </para>
    /// <para>
    /// The deploy command is marked auto-runnable only on Windows: under Proton it would
    /// have to run inside the prefix, which is out of scope, so there the command is printed
    /// for the user to run.
    /// </para>
    /// </remarks>
    public override LaunchRequirements GetLaunchRequirements(
        GameInstallation installation,
        IReadOnlyList<DeployedMod> mods)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(mods);

        var redModNames = RedModNames(mods);

        var launchArguments = redModNames.Count > 0
            ? new[] { ModdedLaunchArgument }
            : Array.Empty<string>();

        var overrides = new List<string>();

        // RED4ext installs itself as bin/x64/winmm.dll and CET as version.dll or d3d11.dll;
        // under Proton a proxy DLL is ignored unless it is overridden native-first.
        if (HasDeployedFile(mods, "bin/x64/winmm.dll"))
            overrides.Add("winmm");
        if (HasDeployedFile(mods, "bin/x64/version.dll"))
            overrides.Add("version");
        if (HasDeployedFile(mods, "bin/x64/d3d11.dll"))
            overrides.Add("d3d11");

        var actions = new List<PostDeployAction>();

        if (redModNames.Count > 0)
        {
            var deployer = Path.Combine(
                installation.InstallPath, "tools", "redmod", "bin", "redMod.exe");

            var arguments = new List<string>
            {
                "deploy",
                $"-root={installation.InstallPath}"
            };

            arguments.AddRange(redModNames.Select(name => $"-mod={name}"));

            actions.Add(new PostDeployAction(
                "Deploy REDmods so the game can see them",
                deployer,
                arguments,
                // Only runnable directly on Windows: under Proton it needs the prefix.
                AutoRunnable: OperatingSystem.IsWindows() && File.Exists(deployer)));
        }

        return new LaunchRequirements
        {
            LaunchArguments = launchArguments,
            WineDllOverrides = installation.IsProton ? overrides : Array.Empty<string>(),
            PostDeployActions = actions
        };
    }

    /// <summary>
    /// Checks a staged mod against Cyberpunk's layout rules.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <param name="stagedMod">The staged mod.</param>
    /// <returns>Diagnostics; errors block the deploy.</returns>
    /// <remarks>
    /// These are the three mistakes that make a mod install cleanly and then do nothing:
    /// an <c>.archive</c> in a subfolder of <c>archive/pc/mod</c> (the game only reads that
    /// folder directly), a REDmod with no <c>info.json</c>, and a CET mod folder with no
    /// <c>init.lua</c>.
    /// </remarks>
    public override IReadOnlyList<ModDiagnostic> ValidateStaged(
        GameInstallation installation,
        StagedMod stagedMod)
    {
        ArgumentNullException.ThrowIfNull(stagedMod);

        var diagnostics = new List<ModDiagnostic>();
        var gameFiles = stagedMod.Files
            .Where(f => string.Equals(f.RootName, ModRoots.Game, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.RelativePath)
            .ToList();

        foreach (var path in gameFiles)
        {
            if (!path.EndsWith(".archive", StringComparison.OrdinalIgnoreCase))
                continue;

            var folder = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? string.Empty;

            if (folder.StartsWith(LegacyArchiveFolder, StringComparison.OrdinalIgnoreCase) &&
                folder.Length > LegacyArchiveFolder.Length)
            {
                diagnostics.Add(new ModDiagnostic
                {
                    Severity = DiagnosticSeverity.Error,
                    Code = "cp77.nested_archive",
                    Message =
                        $"'{path}' is nested below {LegacyArchiveFolder}/. The game only loads " +
                        "archives placed directly in that folder, so this mod would do nothing.",
                    RelativePath = path,
                    Remedy = $"Move the .archive file directly into {LegacyArchiveFolder}/."
                });
            }
        }

        // A REDmod is a folder under mods/ — the game identifies it by its info.json.
        foreach (var modFolder in TopLevelFoldersUnder(gameFiles, "mods/"))
        {
            var hasInfo = gameFiles.Any(f =>
                f.StartsWith($"mods/{modFolder}/", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(f).Equals("info.json", StringComparison.OrdinalIgnoreCase));

            if (!hasInfo)
            {
                diagnostics.Add(ModDiagnostic.Warning(
                    "cp77.redmod_missing_info",
                    $"'mods/{modFolder}' has no info.json, so the game will not recognise it " +
                    "as a REDmod.",
                    $"mods/{modFolder}"));
            }
        }

        // Each CET mod is a folder with an init.lua; without it CET skips the folder.
        const string cetRoot = "bin/x64/plugins/cyber_engine_tweaks/mods/";

        foreach (var modFolder in TopLevelFoldersUnder(gameFiles, cetRoot))
        {
            var hasInit = gameFiles.Any(f =>
                f.StartsWith($"{cetRoot}{modFolder}/", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(f).Equals("init.lua", StringComparison.OrdinalIgnoreCase));

            if (!hasInit)
            {
                diagnostics.Add(ModDiagnostic.Warning(
                    "cp77.cet_missing_init",
                    $"CET mod '{modFolder}' has no init.lua, so Cyber Engine Tweaks will not " +
                    "load it.",
                    cetRoot + modFolder));
            }
        }

        return diagnostics;
    }

    /// <summary>
    /// The deployed <c>.archive</c> filenames in engine priority order.
    /// </summary>
    /// <param name="mods">Deployed mods in Modular order.</param>
    /// <returns>Filenames, highest engine priority first.</returns>
    private static List<string> ManagedArchiveNames(IReadOnlyList<DeployedMod> mods) =>
        mods
            // Reverse: Modular's last mod wins, and for legacy archives that means it must
            // be loaded first.
            .OrderByDescending(m => m.OrderIndex)
            .SelectMany(m => m.DeployedFiles)
            .Where(f =>
                string.Equals(f.RootName, ModRoots.Game, StringComparison.OrdinalIgnoreCase) &&
                f.RelativePath.EndsWith(".archive", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    Path.GetDirectoryName(f.RelativePath)?.Replace('\\', '/'),
                    LegacyArchiveFolder,
                    StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetFileName(f.RelativePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The REDmod folder names a deployment contains, in Modular order.
    /// </summary>
    /// <param name="mods">Deployed mods.</param>
    /// <returns>REDmod names for <c>redMod.exe deploy -mod=</c> arguments.</returns>
    private static List<string> RedModNames(IReadOnlyList<DeployedMod> mods) =>
        mods
            .OrderBy(m => m.OrderIndex)
            .SelectMany(m => m.DeployedFiles)
            .Where(f =>
                string.Equals(f.RootName, ModRoots.Game, StringComparison.OrdinalIgnoreCase) &&
                f.RelativePath.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(f.RelativePath)
                    .Equals("info.json", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.RelativePath.Split('/')[1])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool HasDeployedFile(IReadOnlyList<DeployedMod> mods, string relativePath) =>
        mods.Any(m => m.DeployedFiles.Any(f =>
            string.Equals(f.RootName, ModRoots.Game, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(f.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Names of the immediate subfolders of a prefix, as seen in a file list.
    /// </summary>
    /// <param name="paths">Root-relative file paths.</param>
    /// <param name="prefix">Prefix ending in a slash.</param>
    /// <returns>The distinct folder names directly under the prefix.</returns>
    private static IEnumerable<string> TopLevelFoldersUnder(
        IReadOnlyList<string> paths, string prefix) =>
        paths
            .Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                        p.Length > prefix.Length)
            .Select(p => p[prefix.Length..].Split('/')[0])
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);
}
