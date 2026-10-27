using Modular.Core.Archives;
using Modular.Core.Installers.Cyberpunk;
using Modular.Sdk;
using Modular.Sdk.Archives;
using Modular.Sdk.Compatibility;
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

    // First line of every modlist.txt Modular writes, which is how it knows its own.
    private const string ModListHeader =
        "# Written by Modular. Highest priority first: for legacy archives the first";

    /// <summary>Launch argument Cyberpunk needs before it will load any REDmod.</summary>
    public const string ModdedLaunchArgument = "-modded";

    /// <summary>Folder holding one subfolder per Cyber Engine Tweaks mod.</summary>
    public const string CetModsFolder = "bin/x64/plugins/cyber_engine_tweaks/mods/";

    private const string Red4ExtPluginsFolder = "red4ext/plugins/";
    private const string RedscriptFolder = "r6/scripts/";
    private const string TweaksFolder = "r6/tweaks/";

    // The top-level game folders mods write into. Only these are scanned for dependencies:
    // the rest of the install is the game's own and never needs reading.
    private static readonly string[] ModRootFolders = ["archive", "bin", "engine", "mods", "r6", "red4ext"];

    // The frameworks other mods are built on, each known by a file only it installs. The
    // names are the ones their NexusMods pages use, so a requirement listed there matches.
    private static readonly CyberpunkFramework Red4Ext =
        new("red4ext", "RED4ext", ["red4ext/RED4ext.dll", "bin/x64/winmm.dll"]);
    private static readonly CyberpunkFramework CyberEngineTweaks =
        new("cet", "Cyber Engine Tweaks", ["bin/x64/plugins/cyber_engine_tweaks.asi"]);
    private static readonly CyberpunkFramework Redscript =
        new("redscript", "redscript", ["engine/tools/scc.exe"]);
    private static readonly CyberpunkFramework ArchiveXl =
        new("archivexl", "ArchiveXL", ["red4ext/plugins/ArchiveXL/ArchiveXL.dll"]);
    private static readonly CyberpunkFramework TweakXl =
        new("tweakxl", "TweakXL", ["red4ext/plugins/TweakXL/TweakXL.dll"]);
    private static readonly CyberpunkFramework Codeware =
        new("codeware", "Codeware", ["red4ext/plugins/Codeware/Codeware.dll"]);

    private static readonly CyberpunkFramework[] Frameworks =
        [Red4Ext, CyberEngineTweaks, Redscript, ArchiveXl, TweakXl, Codeware];

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
    /// Files that mark Cyberpunk's loaders, and mods with native code tied to the exe.
    /// </summary>
    /// <remarks>
    /// RED4ext, Cyber Engine Tweaks and redscript are what every other code mod runs
    /// through, so mod pages rarely list them. A RED4ext plugin is built against one game
    /// executable; after a game update it fails to load, or crashes the game, until its
    /// author rebuilds it.
    /// </remarks>
    public override IReadOnlyList<CoreModRule> GetCoreModRules() =>
    [
        new("red4ext/RED4ext.dll", CoreModRole.Loader, "RED4ext plugin loader"),
        new("bin/x64/winmm.dll", CoreModRole.Loader, "RED4ext plugin loader"),
        new("bin/x64/plugins/cyber_engine_tweaks.asi", CoreModRole.Loader, "Cyber Engine Tweaks, which runs CET mods"),
        new("engine/tools/scc.exe", CoreModRole.Loader, "redscript compiler, which builds r6/scripts"),
        new("red4ext/plugins/**/*.dll", CoreModRole.NativeCode, "RED4ext plugin built against the game executable")
    ];

    /// <summary>
    /// Where Cyberpunk mods keep code and game data that a patch can break.
    /// </summary>
    /// <remarks>
    /// Scripts and tweaks name game classes and records that patches rename; plugins and
    /// loaders hook the executable; REDmod mods carry scripts and tweaks too. Archives in
    /// <c>archive/pc/mod</c> hold meshes and textures, which patches leave alone, and
    /// ArchiveXL <c>.xl</c> files only point at them.
    /// </remarks>
    public override IReadOnlyList<string> GetUpdateSensitivePaths() =>
    [
        "bin/**",
        "engine/**",
        "red4ext/**",
        "r6/scripts/**",
        "r6/tweaks/**",
        "r6/config/**",
        "r6/input/**",
        "mods/**"
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

        // With no archive left there is no order to keep, and an empty list would be the
        // last thing in the game's mod folder after every mod is gone. Only a list this
        // method wrote is removed; one the user wrote is theirs.
        if (managed.Count == 0 && unmanaged.Count == 0)
        {
            if (File.Exists(manifestPath) &&
                (await File.ReadAllTextAsync(manifestPath, ct)).StartsWith(ModListHeader, StringComparison.Ordinal))
            {
                File.Delete(manifestPath);
            }

            return;
        }

        var lines = new List<string>
        {
            ModListHeader,
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
        const string cetRoot = CetModsFolder;

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
    /// Finds mods on disk whose file types need a framework that is not installed.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <returns>One entry per dependent mod and missing framework.</returns>
    /// <remarks>
    /// <para>
    /// Cyberpunk mods carry no dependency metadata, but most of what they need follows from
    /// where their files go: a RED4ext plugin needs RED4ext, a CET mod needs Cyber Engine
    /// Tweaks, <c>.reds</c> sources need redscript, <c>.xl</c> files need ArchiveXL and
    /// tweak files need TweakXL. Requirements that leave no trace in the files, such as
    /// Codeware, are only known from the mod's page.
    /// </para>
    /// <para>
    /// The frameworks are themselves RED4ext plugins, so ArchiveXL without RED4ext is
    /// reported as well.
    /// </para>
    /// </remarks>
    public override IReadOnlyList<UnmetDependency> FindUnmetDependencies(
        GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);

        var files = ScanModFiles(installation.InstallPath);
        if (files.Count == 0)
            return Array.Empty<UnmetDependency>();

        var present = files.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return FindDependents(files)
            .Where(d => !d.Framework.IsPresent(present))
            .Select(d => new UnmetDependency(d.Name, d.File, d.Framework.Name, d.Framework.Id))
            .ToList();
    }

    /// <summary>
    /// Names the mods present in the game folder, whoever installed them.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <returns>
    /// The installed frameworks by their NexusMods names, then the folder names of RED4ext
    /// plugins, CET mods, redscript mods, tweaks and REDmods, and the names of legacy archives.
    /// </returns>
    /// <remarks>
    /// Cyberpunk mods have no name of their own besides their folder or file, which is
    /// usually the mod's name in some spelling. The caller compares names with case and
    /// punctuation removed, so <c>input_loader</c> matches "Input Loader".
    /// </remarks>
    public override IReadOnlyList<string> ListPresentModNames(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);

        var files = ScanModFiles(installation.InstallPath);
        if (files.Count == 0)
            return Array.Empty<string>();

        var present = files.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var names = Frameworks.Where(f => f.IsPresent(present)).Select(f => f.Name).ToList();

        foreach (var folder in new[] { Red4ExtPluginsFolder, CetModsFolder, RedscriptFolder, TweaksFolder, "mods/" })
            names.AddRange(GroupUnder(files, folder, _ => true).Select(u => u.Name));

        names.AddRange(files
            .Where(f => IsDirectlyIn(f, LegacyArchiveFolder) &&
                        f.EndsWith(".archive", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>());

        return names
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Every mod on disk that needs a framework, with the framework it needs.
    /// </summary>
    /// <param name="files">Root-relative paths under the mod folders, in ordinal order.</param>
    /// <returns>The dependents, each with one of its files to identify which mod it is.</returns>
    private static IEnumerable<CyberpunkDependent> FindDependents(IReadOnlyList<string> files)
    {
        // A RED4ext plugin is a folder (or a lone DLL) under red4ext/plugins.
        foreach (var unit in GroupUnder(files, Red4ExtPluginsFolder, f => HasExtension(f, ".dll")))
            yield return new CyberpunkDependent(unit.Name, unit.File, Red4Ext);

        // A CET mod is a folder with an init.lua; CET's own files sit outside mods/.
        foreach (var folder in TopLevelFoldersUnder(files, CetModsFolder))
        {
            var modFiles = files
                .Where(f => f.StartsWith($"{CetModsFolder}{folder}/", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (!modFiles.Any(f => IsNamed(f, "init.lua")))
                continue;

            // Every CET mod has an init.lua, so any other file says better which mod it is.
            var file = modFiles.FirstOrDefault(f => !IsNamed(f, "init.lua")) ?? modFiles[0];
            yield return new CyberpunkDependent(folder, Path.GetFileName(file), CyberEngineTweaks);
        }

        foreach (var unit in GroupUnder(files, RedscriptFolder, f => HasExtension(f, ".reds")))
            yield return new CyberpunkDependent(unit.Name, unit.File, Redscript);

        foreach (var unit in GroupUnder(files, TweaksFolder,
                     f => HasExtension(f, ".yaml") || HasExtension(f, ".yml") || HasExtension(f, ".tweak")))
        {
            yield return new CyberpunkDependent(unit.Name, unit.File, TweakXl);
        }

        // ArchiveXL reads .xl files beside the archives, usually named <archive>.archive.xl.
        foreach (var file in files.Where(f =>
                     IsDirectlyIn(f, LegacyArchiveFolder) && HasExtension(f, ".xl")))
        {
            var name = Path.GetFileName(file);
            var stem = name[..^".xl".Length];
            if (stem.EndsWith(".archive", StringComparison.OrdinalIgnoreCase))
                stem = stem[..^".archive".Length];

            yield return new CyberpunkDependent(stem, name, ArchiveXl);
        }
    }

    /// <summary>
    /// Groups the matching files below a folder by the mod they belong to.
    /// </summary>
    /// <param name="files">Root-relative paths in ordinal order.</param>
    /// <param name="prefix">Folder holding one mod per subfolder, ending in a slash.</param>
    /// <param name="matches">Which files count.</param>
    /// <returns>
    /// One entry per subfolder, or per file placed directly in the folder, with the file
    /// name of its first matching file.
    /// </returns>
    private static IEnumerable<(string Name, string File)> GroupUnder(
        IReadOnlyList<string> files, string prefix, Func<string, bool> matches) =>
        files
            .Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                        f.Length > prefix.Length && matches(f))
            .GroupBy(f =>
            {
                var rest = f[prefix.Length..];
                var slash = rest.IndexOf('/');
                return slash >= 0 ? rest[..slash] : Path.GetFileNameWithoutExtension(rest);
            }, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, Path.GetFileName(g.First())));

    /// <summary>
    /// Lists every file under the folders mods write into.
    /// </summary>
    /// <param name="installPath">The game root.</param>
    /// <returns>Root-relative paths with forward slashes, in ordinal order.</returns>
    /// <remarks>
    /// Folder names are matched without case: a mod packed with <c>R6/</c> or
    /// <c>Red4ext/</c> still works under Proton, so it still counts here.
    /// </remarks>
    private static List<string> ScanModFiles(string installPath)
    {
        var files = new List<string>();

        try
        {
            if (string.IsNullOrEmpty(installPath) || !Directory.Exists(installPath))
                return files;

            foreach (var root in Directory.EnumerateDirectories(installPath))
            {
                if (!ModRootFolders.Contains(Path.GetFileName(root), StringComparer.OrdinalIgnoreCase))
                    continue;

                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    files.Add(Path.GetRelativePath(installPath, file).Replace('\\', '/'));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable folder cannot be checked; the check is advisory.
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static bool IsDirectlyIn(string path, string folder)
    {
        var slash = path.LastIndexOf('/');
        return slash >= 0 && string.Equals(path[..slash], folder, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExtension(string path, string extension) =>
        path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

    private static bool IsNamed(string path, string name) =>
        string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase);

    /// <summary>A framework other mods need, known by the files only it installs.</summary>
    /// <param name="Id">Identifier reported for the missing framework.</param>
    /// <param name="Name">The framework's name on NexusMods.</param>
    /// <param name="Markers">Root-relative files; any one of them means it is installed.</param>
    private sealed record CyberpunkFramework(string Id, string Name, string[] Markers)
    {
        public bool IsPresent(IReadOnlySet<string> files) => Markers.Any(files.Contains);
    }

    /// <summary>A mod on disk that needs a framework.</summary>
    /// <param name="Name">The mod's folder or file name.</param>
    /// <param name="File">File name of one of its files.</param>
    /// <param name="Framework">The framework it needs.</param>
    private sealed record CyberpunkDependent(string Name, string File, CyberpunkFramework Framework);

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
