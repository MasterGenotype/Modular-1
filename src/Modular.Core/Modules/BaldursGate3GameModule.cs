using System.Diagnostics;
using Modular.Core.Archives;
using Modular.Core.Installers.BaldursGate3;
using Modular.Core.Metadata;
using Modular.Core.Utilities;
using Modular.Sdk;
using Modular.Sdk.Archives;
using Modular.Sdk.Installers;
using Modular.Sdk.Modules;

namespace Modular.Core.Modules;

/// <summary>
/// Game module for Baldur's Gate 3 (Steam AppID 1086940).
/// </summary>
/// <remarks>
/// <para>
/// BG3 mods live in two places that have nothing to do with each other. A <c>.pak</c> goes
/// in the user <c>Mods</c> folder, which is under the user's local data rather than the
/// install; loose files go beneath <c>&lt;install&gt;/Data/</c>. The two never share a
/// folder, so a pak and a loose file can never overwrite one another on disk.
/// </para>
/// <para>
/// They can still fight inside the engine: a loose file under <c>Data/</c> overrides the
/// same virtual path in every pak, whatever the load order says. No ordering can express
/// that, so <see cref="ValidateStaged"/> reads pak file tables and reports it.
/// </para>
/// <para>
/// Pak order is the list in <c>modsettings.lsx</c> and nothing else; the game does not
/// look at file names. <see cref="WriteLoadOrderAsync"/> maintains that list. The prefix
/// <see cref="MapDeployPath"/> puts on each pak shows the same band in the folder, for a
/// person browsing it, and has no effect on the game.
/// </para>
/// </remarks>
public sealed partial class BaldursGate3GameModule : GameModuleBase
{
    private readonly IArchiveReaderFactory _archiveReaderFactory;
    private readonly IModCategorySource? _categories;

    /// <summary>Name of the module root holding the user <c>Mods</c> folder.</summary>
    public const string ModsRoot = BG3ArchiveAnalyzer.ModsRoot;

    /// <summary>The user <c>Mods</c> folder, relative to the user's local data folder.</summary>
    public const string ModsRelativeToLocalAppData = "Larian Studios/Baldur's Gate 3/Mods";

    /// <summary>The native Linux executable, relative to the game root.</summary>
    public const string NativeExecutable = "bin/bg3";

    /// <summary>The Script Extender's loader, relative to the game root.</summary>
    public const string ScriptExtenderPath = "bin/" + BG3ArchiveAnalyzer.ScriptExtenderLoader;

    /// <summary>Where Script Extender releases are published.</summary>
    public const string ScriptExtenderUrl = "https://github.com/Norbyte/bg3se/releases";

    // How many shadowed paths a single diagnostic names before it just counts the rest.
    private const int MaxExamples = 3;

    // The game's processes, by the names the Windows and native builds run under.
    private static readonly string[] GameProcessNames = ["bg3", "bg3_dx11", "bg3.exe", "bg3_dx11.exe"];

    /// <summary>Creates the module.</summary>
    /// <param name="archiveReaderFactory">Shared archive reader factory.</param>
    /// <param name="categories">
    /// Says which category a mod hub files a mod under, which decides its load-order band
    /// when known. Without it the band comes from the pak's contents alone.
    /// </param>
    public BaldursGate3GameModule(
        IArchiveReaderFactory? archiveReaderFactory = null,
        IModCategorySource? categories = null)
    {
        _archiveReaderFactory = archiveReaderFactory ?? new ArchiveReaderFactory();
        _categories = categories;
    }

    /// <summary>
    /// Whether the game is running. Replaceable so tests need no game process.
    /// </summary>
    internal Func<bool> IsGameRunning { get; set; } = DetectRunningGame;

    /// <inheritdoc />
    public override string ModuleId => "baldursgate3";

    /// <inheritdoc />
    public override string DisplayName => "Baldur's Gate 3";

    /// <inheritdoc />
    public override IReadOnlyList<string> GameIds { get; } =
        [Sdk.GameIds.BaldursGate3, "1086940"];

    /// <inheritdoc />
    public override IReadOnlyList<int> SteamAppIds { get; } = [1086940];

    /// <inheritdoc />
    protected override IReadOnlyList<string> PrimaryAnchors { get; } =
        ["bin", "Data/Gustav.pak"];

    /// <inheritdoc />
    protected override IReadOnlyList<string> SupportingAnchors { get; } =
        ["bin/bg3.exe", NativeExecutable, "Data/Shared.pak"];

    /// <summary>
    /// Adds the user <c>Mods</c> folder as a root.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <returns>The <see cref="ModsRoot"/> root.</returns>
    /// <remarks>
    /// The native Linux build and the Windows build under Proton keep separate user data,
    /// and Steam leaves a Proton prefix behind when a game is switched back to native. The
    /// executable on disk is therefore what decides: with <c>bin/bg3</c> present the game is
    /// running natively and reads <c>~/.local/share</c>, whether or not a prefix exists.
    /// </remarks>
    protected override IReadOnlyDictionary<string, string>? GetExtraRoots(
        GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);

        var userData = ResolveUserDataDirectory(installation);
        if (userData == null)
            return null;

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ModsRoot] = Path.Combine(userData, "Mods")
        };
    }

    /// <summary>
    /// Finds the game's user data folder, which holds <c>Mods</c> and <c>PlayerProfiles</c>.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <returns>The folder, or <c>null</c> when it cannot be located.</returns>
    public static string? ResolveUserDataDirectory(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);

        var isNative = File.Exists(Path.Combine(
            installation.InstallPath,
            NativeExecutable.Replace('/', Path.DirectorySeparatorChar)));

        var localAppData = isNative
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : ModRoots.ResolveStandardRoot(installation, ModRoots.LocalAppData);

        if (string.IsNullOrEmpty(localAppData))
            return null;

        var modsFolder = Path.Combine(
            localAppData,
            ModsRelativeToLocalAppData.Replace('/', Path.DirectorySeparatorChar));

        return Path.GetDirectoryName(modsFolder);
    }

    /// <summary>
    /// Path of the file holding the load order.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <returns>The path, or <c>null</c> when the user data folder cannot be located.</returns>
    public static string? ResolveModSettingsPath(GameInstallation installation)
    {
        var userData = ResolveUserDataDirectory(installation);

        return userData == null
            ? null
            : Path.Combine(
                userData,
                BG3ModSettings.ProfileFolder.Replace('/', Path.DirectorySeparatorChar),
                BG3ModSettings.FileName);
    }

    /// <inheritdoc />
    public override IReadOnlyList<IModInstaller> GetInstallers() =>
        [new BG3ModInstaller(_archiveReaderFactory)];

    /// <summary>
    /// Prefixes each pak with its load-order band and its position within that band.
    /// </summary>
    /// <param name="context">The staged file plus its ordering information.</param>
    /// <returns>Where the file should actually be deployed.</returns>
    /// <remarks>
    /// <para>
    /// The prefix is <c>&lt;band&gt;-&lt;order&gt;_</c>, such as <c>30-004_</c>, and is for
    /// people: it makes the folder list the way the load order runs. The game ignores
    /// file names, so the order itself is set by <see cref="WriteLoadOrderAsync"/>, from
    /// the same band.
    /// </para>
    /// <para>
    /// Only paks are renamed. Loose files keep the exact path the game looks them up by,
    /// and are not part of the pak order at all.
    /// </para>
    /// </remarks>
    public override DeployTarget MapDeployPath(DeployPathContext context)
    {
        if (!IsModPak(context.RootName, context.RelativePath))
            return new DeployTarget(context.RootName, context.RelativePath);

        // A pak taken from an earlier deployment already carries an order prefix; adding a
        // second one would stack them on every reinstall.
        var fileName = ExistingOrderPrefix().Replace(Path.GetFileName(context.RelativePath), string.Empty);

        var profile = context.StagedFilePath != null
            ? BG3PakProfile.Read(ProfileSourceOf(context.StagedFilePath))
            : null;
        var tier = BG3LoadOrderClassifier.Classify(profile, CategoryOf(context.ModId));

        // Pad to the width the whole list needs, so filenames sort the same way numbers do.
        var width = Math.Max(3, context.EnabledModCount.ToString().Length);
        var order = context.LoadPosition.ToString(
            new string('0', width), System.Globalization.CultureInfo.InvariantCulture);

        return new DeployTarget(context.RootName, $"{tier.Rank:00}-{order}_{fileName}");
    }

    /// <summary>
    /// Writes Modular's paks into <c>modsettings.lsx</c>, the list the game loads mods from.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <param name="mods">Deployed mods in Modular order.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the file is up to date.</returns>
    /// <remarks>
    /// Entries Modular did not add, such as those of mods installed from the game's own
    /// mod manager, are kept where they are; see <see cref="BG3ModSettings"/> for the
    /// rules. Nothing is written while the game is running, because the game writes the
    /// same file when it exits and would discard the change.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The game is running, the file cannot be located, or part of the order could not be
    /// put right. The deployer reports the message as a warning.
    /// </exception>
    public override Task WriteLoadOrderAsync(
        GameInstallation installation,
        IReadOnlyList<DeployedMod> mods,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(mods);

        var modsDirectory = ResolveModPaths(installation).GetRoot(ModsRoot);
        var modSettingsPath = ResolveModSettingsPath(installation);

        if (modsDirectory == null || modSettingsPath == null)
            throw new InvalidOperationException("the game's user data folder could not be located");

        var managed = new List<BG3ManagedPak>();

        foreach (var mod in mods.Where(m => m.Enabled))
        {
            foreach (var file in mod.DeployedFiles.Where(f => IsModPak(f.RootName, f.RelativePath)))
            {
                ct.ThrowIfCancellationRequested();

                var profile = BG3PakProfile.Read(Path.Combine(
                    modsDirectory, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));

                // A pak with no module of its own is an override; the game applies it
                // without it being listed.
                if (profile?.Meta == null)
                    continue;

                managed.Add(new BG3ManagedPak(
                    profile.Meta.Module,
                    BG3LoadOrderClassifier.Classify(profile, CategoryOf(mod.ModId)),
                    mod.LoadPosition));
            }
        }

        // With nothing of Modular's deployed and no record of ever having written here,
        // the file is entirely someone else's and is left alone.
        if (managed.Count == 0 && !BG3ModSettings.HasOwnershipRecord(modSettingsPath))
            return Task.CompletedTask;

        BG3ModSettingsResult result;
        try
        {
            result = BG3ModSettings.Update(
                modSettingsPath, ReadInstalledMetas(modsDirectory), managed, () => !IsGameRunning());
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException(
                "Baldur's Gate 3 is running, and rewrites modsettings.lsx when it exits, so " +
                "the load order was not updated. Close the game and deploy again.");
        }

        if (result.Warnings.Count > 0)
        {
            throw new InvalidOperationException(
                "modsettings.lsx is up to date, but " + string.Join(" ", result.Warnings));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override EngineOrderDescription? DescribeEngineOrder(
        GameInstallation installation,
        IReadOnlyList<DeployedMod> mods)
    {
        ArgumentNullException.ThrowIfNull(installation);

        var modSettingsPath = ResolveModSettingsPath(installation);
        if (modSettingsPath == null)
            return null;

        var order = BG3ModSettings.ReadOrder(modSettingsPath);
        if (order.Count == 0)
            return null;

        return new EngineOrderDescription(
            "the order listed in modsettings.lsx; a later mod wins. Modular adds its paks by " +
            "band (framework, interface, race, class, gameplay, item, appearance, patch), " +
            "leaves every entry already listed where it is, and moves a mod only to put " +
            "it after something it depends on",
            order,
            modSettingsPath);
    }

    /// <summary>
    /// The game keeps its own load order, in <c>modsettings.lsx</c>, which the in-game mod
    /// manager and other tools also write.
    /// </summary>
    public override LoadOrderKind LoadOrderKind => LoadOrderKind.External;

    /// <summary>
    /// Reads <c>modsettings.lsx</c> as it stands, saying which entries are Modular's.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <param name="mods">The game's mods, to match entries to them.</param>
    /// <returns>Every entry, or <c>null</c> when the file cannot be found.</returns>
    /// <remarks>
    /// An entry is a Modular mod's when one of that mod's deployed paks declares the
    /// entry's module. GustavX, the base campaign, is locked first.
    /// </remarks>
    public override EngineOrder? ReadEngineOrder(
        GameInstallation installation,
        IReadOnlyList<DeployedMod> mods)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(mods);

        var modSettingsPath = ResolveModSettingsPath(installation);
        if (modSettingsPath == null)
            return null;

        var modsDirectory = ResolveModPaths(installation).GetRoot(ModsRoot);
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (modsDirectory != null)
        {
            foreach (var mod in mods)
            {
                foreach (var file in mod.DeployedFiles.Where(f => IsModPak(f.RootName, f.RelativePath)))
                {
                    var meta = BG3PakProfile.Read(Path.Combine(
                        modsDirectory, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)))?.Meta;

                    if (meta != null)
                        owners.TryAdd(meta.Module.Uuid, mod.ModId);
                }
            }
        }

        var entries = BG3ModSettings.ReadEntries(modSettingsPath)
            .Select(e => new EngineOrderEntry(
                e.Uuid,
                e.Name,
                owners.GetValueOrDefault(e.Uuid),
                Locked: string.Equals(e.Uuid, BG3ModSettings.GustavXUuid, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return new EngineOrder(entries, modSettingsPath);
    }

    /// <summary>
    /// Rewrites the order of <c>modsettings.lsx</c> to the user's choice.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <param name="entryIds">Every entry's module UUID, first loaded to last.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the file is written.</returns>
    /// <exception cref="InvalidOperationException">
    /// The game is running, the file cannot be found, or the order is refused; see
    /// <see cref="BG3ModSettings.SetOrder"/>.
    /// </exception>
    public override Task SetEngineOrderAsync(
        GameInstallation installation,
        IReadOnlyList<string> entryIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(entryIds);

        var modsDirectory = ResolveModPaths(installation).GetRoot(ModsRoot);
        var modSettingsPath = ResolveModSettingsPath(installation);

        if (modsDirectory == null || modSettingsPath == null)
            throw new InvalidOperationException("the game's user data folder could not be located");

        // Checked first, so the user hears the reason that applies whatever the order is.
        if (IsGameRunning())
        {
            throw new InvalidOperationException(
                "Baldur's Gate 3 is running, and rewrites modsettings.lsx when it exits, so the " +
                "load order was not changed. Close the game and try again.");
        }

        BG3ModSettings.SetOrder(
            modSettingsPath, entryIds, ReadInstalledMetas(modsDirectory), () => !IsGameRunning());

        return Task.CompletedTask;
    }

    /// <summary>
    /// Says what the game needs at launch for the deployed mods to load.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <param name="mods">The deployed mods.</param>
    /// <returns>
    /// The <c>DWrite</c> override when Script Extender is present under Proton, where a
    /// proxy DLL beside the executable is ignored unless it is overridden native-first.
    /// </returns>
    public override LaunchRequirements GetLaunchRequirements(
        GameInstallation installation,
        IReadOnlyList<DeployedMod> mods)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(mods);

        var deployed = mods.Any(m => m.DeployedFiles.Any(f =>
            string.Equals(f.RootName, ModRoots.Game, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(f.RelativePath, ScriptExtenderPath, StringComparison.OrdinalIgnoreCase)));

        return installation.IsProton && (deployed || IsScriptExtenderInstalled(installation))
            ? new LaunchRequirements { WineDllOverrides = ["dwrite"] }
            : LaunchRequirements.None;
    }

    /// <summary>
    /// Whether the Script Extender's loader is in place beside the game executable.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <returns>True when <c>bin/DWrite.dll</c> exists, whatever its casing.</returns>
    public static bool IsScriptExtenderInstalled(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);

        var bin = Path.Combine(installation.InstallPath, "bin");

        try
        {
            return Directory.Exists(bin) &&
                   Directory.EnumerateFiles(bin).Any(f => string.Equals(
                       Path.GetFileName(f),
                       BG3ArchiveAnalyzer.ScriptExtenderLoader,
                       StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks a staged mod against Script Extender and against files that would shadow it.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <param name="stagedMod">The staged mod.</param>
    /// <returns>
    /// An error when the mod carries Script Extender scripts and Script Extender is not
    /// installed, since it would do nothing; warnings otherwise.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Script Extender is flagged on every install while it is missing, because most of
    /// the game's mods are written against it. Only a mod that provably needs it is
    /// refused, and the Script Extender archive itself is never refused.
    /// </para>
    /// <para>
    /// Shadowing is checked in both directions against what is already on disk: a staged
    /// pak against the loose files in <c>Data/</c>, and staged loose files against the
    /// paks in the user <c>Mods</c> folder. The loose file wins in every case, and no
    /// position in the load order changes that, so the only useful thing to do is say
    /// which mod is being shadowed.
    /// </para>
    /// </remarks>
    public override IReadOnlyList<ModDiagnostic> ValidateStaged(
        GameInstallation installation,
        StagedMod stagedMod)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(stagedMod);

        var diagnostics = new List<ModDiagnostic>();
        var dataDirectory = Path.Combine(installation.InstallPath, BG3ArchiveAnalyzer.DataFolder);

        var stagedPaks = stagedMod.Files
            .Where(f => IsModPak(f.RootName, f.RelativePath))
            .Select(f => (
                File: f,
                Profile: BG3PakProfile.Read(Path.Combine(
                    stagedMod.StagingPath,
                    f.RootName,
                    f.RelativePath.Replace('/', Path.DirectorySeparatorChar)))))
            .ToList();

        CheckScriptExtender(installation, stagedMod, stagedPaks, diagnostics);

        // Staged paks against loose files already in Data/. The game finds files without
        // regard to case, so the comparison cannot depend on the filesystem's.
        var looseOnDisk = stagedPaks.Count > 0
            ? ListFilesBeneath(dataDirectory)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (pak, profile) in stagedPaks)
        {
            if (profile == null || looseOnDisk.Count == 0)
                continue;

            var shadowed = profile.Files.Where(looseOnDisk.Contains).ToList();

            if (shadowed.Count > 0)
            {
                diagnostics.Add(ModDiagnostic.Warning(
                    "bg3.pak_shadowed_by_loose_file",
                    $"{Describe(shadowed)} in '{Path.GetFileName(pak.RelativePath)}' already " +
                    "exist as loose files under Data/. Loose files override every pak " +
                    "regardless of load order, so the pak's copies will not be used.",
                    pak.RelativePath));
            }
        }

        // Staged loose files against the paks already in the user Mods folder.
        var looseFiles = stagedMod.Files
            .Where(f => string.Equals(f.RootName, ModRoots.Game, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.RelativePath.Replace('\\', '/'))
            .Where(p => p.StartsWith(
                BG3ArchiveAnalyzer.DataFolder + "/", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[(BG3ArchiveAnalyzer.DataFolder.Length + 1)..])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var modsDirectory = ResolveModPaths(installation).GetRoot(ModsRoot);

        if (looseFiles.Count > 0 && modsDirectory != null && Directory.Exists(modsDirectory))
        {
            foreach (var pakPath in Directory
                         .EnumerateFiles(modsDirectory, "*.pak")
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                var shadowed = (BG3PakProfile.Read(pakPath)?.Files ?? [])
                    .Where(looseFiles.Contains)
                    .ToList();

                if (shadowed.Count == 0)
                    continue;

                diagnostics.Add(ModDiagnostic.Warning(
                    "bg3.loose_file_shadows_pak",
                    $"{Describe(shadowed)} from this mod are also packed in " +
                    $"'{Path.GetFileName(pakPath)}'. Loose files under Data/ override every " +
                    "pak regardless of load order, so that pak's copies will stop being used.",
                    $"{BG3ArchiveAnalyzer.DataFolder}/{shadowed[0]}"));
            }
        }

        return diagnostics;
    }

    private static void CheckScriptExtender(
        GameInstallation installation,
        StagedMod stagedMod,
        IReadOnlyList<(StagedFile File, BG3PakProfile? Profile)> stagedPaks,
        List<ModDiagnostic> diagnostics)
    {
        var bringsIt = stagedMod.Files.Any(f =>
            string.Equals(f.RootName, ModRoots.Game, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(f.RelativePath, ScriptExtenderPath, StringComparison.OrdinalIgnoreCase));

        if (bringsIt || IsScriptExtenderInstalled(installation))
            return;

        var dependent = stagedPaks.FirstOrDefault(p => p.Profile?.NeedsScriptExtender == true);

        if (dependent.Profile != null)
        {
            diagnostics.Add(ModDiagnostic.Error(
                "bg3.script_extender_required",
                $"'{Path.GetFileName(dependent.File.RelativePath)}' contains Script Extender " +
                "scripts, and Script Extender is not installed, so the mod would not work. " +
                $"Install Script Extender first ({ScriptExtenderUrl}), then install this mod.",
                dependent.File.RelativePath));
            return;
        }

        diagnostics.Add(ModDiagnostic.Warning(
            "bg3.script_extender_missing",
            "Script Extender is not installed. Most Baldur's Gate 3 mods require it, and it " +
            $"should be installed before them: {ScriptExtenderUrl}"));
    }

    /// <summary>
    /// Finds modules that paks in the user <c>Mods</c> folder depend on and nothing provides.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <returns>One entry per pak and missing module.</returns>
    /// <remarks>
    /// Every pak in the folder is read, including the ones the in-game mod manager put
    /// there. Modules that ship with the game are skipped: most mods list a dozen of them.
    /// </remarks>
    public override IReadOnlyList<UnmetDependency> FindUnmetDependencies(
        GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);

        var modsDirectory = ResolveModPaths(installation).GetRoot(ModsRoot);
        if (modsDirectory == null)
            return Array.Empty<UnmetDependency>();

        var paks = ReadInstalledPaks(modsDirectory);

        var present = paks
            .Select(p => p.Meta.Module.Uuid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unmet = new List<UnmetDependency>();

        foreach (var (file, meta) in paks)
        {
            foreach (var dependency in meta.ModDependencies.Where(d => !present.Contains(d.Uuid)))
            {
                unmet.Add(new UnmetDependency(
                    string.IsNullOrEmpty(meta.Module.Name) ? file : meta.Module.Name,
                    file,
                    string.IsNullOrEmpty(dependency.Name) ? dependency.Uuid : dependency.Name,
                    dependency.Uuid));
            }
        }

        return unmet;
    }

    /// <summary>
    /// Finds installed mods that declare they cannot be loaded together.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <returns>One entry per declaring mod and conflicting mod that is also present.</returns>
    public override IReadOnlyList<DeclaredConflict> FindDeclaredConflicts(
        GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);

        var modsDirectory = ResolveModPaths(installation).GetRoot(ModsRoot);
        if (modsDirectory == null)
            return Array.Empty<DeclaredConflict>();

        var paks = ReadInstalledPaks(modsDirectory);
        var names = paks
            .GroupBy(p => p.Meta.Module.Uuid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Meta.Module.Name, StringComparer.OrdinalIgnoreCase);

        return paks
            .SelectMany(p => p.Meta.Conflicts
                .Where(c => names.ContainsKey(c.Uuid))
                .Select(c => new DeclaredConflict(
                    p.Meta.Module.Name, p.File, names[c.Uuid], c.Uuid)))
            .ToList();
    }

    /// <summary>
    /// Names the mods present in the user <c>Mods</c> folder, whoever installed them.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <returns>The module name of every readable pak.</returns>
    public override IReadOnlyList<string> ListPresentModNames(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);

        var modsDirectory = ResolveModPaths(installation).GetRoot(ModsRoot);

        return modsDirectory == null
            ? Array.Empty<string>()
            : ReadInstalledPaks(modsDirectory)
                .Select(p => p.Meta.Module.Name)
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    private static List<(string File, BG3PakMeta Meta)> ReadInstalledPaks(string modsDirectory)
    {
        if (!Directory.Exists(modsDirectory))
            return [];

        return Directory
            .EnumerateFiles(modsDirectory, "*.pak")
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => (File: Path.GetFileName(p), Meta: BG3PakProfile.Read(p)?.Meta))
            .Where(p => p.Meta != null)
            .Select(p => (p.File, p.Meta!))
            .ToList();
    }

    private static List<BG3PakMeta> ReadInstalledMetas(string modsDirectory) =>
        ReadInstalledPaks(modsDirectory).Select(p => p.Meta).ToList();

    // The category a mod hub files the mod under, when the mod can be traced to one.
    private string? CategoryOf(string modId)
    {
        if (_categories == null)
            return null;

        var nexusId = NexusFileName.ParseModId(modId);

        return nexusId == null
            ? null
            : _categories.GetCategory(Sdk.GameIds.BaldursGate3, nexusId);
    }

    // Paths of every file beneath a folder, relative to it, with forward slashes.
    private static HashSet<string> ListFilesBeneath(string directory)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!Directory.Exists(directory))
                return files;

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                files.Add(Path.GetRelativePath(directory, file).Replace('\\', '/'));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable folder cannot be checked; the check is advisory.
        }

        return files;
    }

    /// <summary>Whether a Baldur's Gate 3 process is running, native or under Proton.</summary>
    internal static bool DetectRunningGame()
    {
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    if (GameProcessNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // If processes cannot be listed there is nothing to go on.
        }

        return false;
    }

    /// <summary>
    /// Whether a file is a pak in the user <c>Mods</c> folder.
    /// </summary>
    /// <param name="rootName">Root the file is under.</param>
    /// <param name="relativePath">Path relative to that root.</param>
    /// <returns>True for a <c>.pak</c> directly under <see cref="ModsRoot"/>.</returns>
    private static bool IsModPak(string rootName, string relativePath) =>
        string.Equals(rootName, ModsRoot, StringComparison.OrdinalIgnoreCase) &&
        relativePath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase);

    private static string Describe(IReadOnlyList<string> paths)
    {
        var examples = string.Join(", ", paths.Take(MaxExamples));

        return paths.Count <= MaxExamples
            ? $"{paths.Count} file(s) ({examples})"
            : $"{paths.Count} files ({examples}, …)";
    }

    /// <summary>
    /// The pak whose contents decide a staged pak's band: itself, or for a later part of a
    /// split pak (<c>Name_1.pak</c>, <c>Name_2.pak</c>, … beside <c>Name.pak</c>), the first part.
    /// </summary>
    /// <param name="stagedPath">The staged pak.</param>
    /// <returns>The path to read the profile from.</returns>
    /// <remarks>
    /// The game finds the later parts by the first part's file name plus <c>_1</c>, <c>_2</c>, …
    /// The later parts carry no header or file list of their own, so read on their own they
    /// would land in a different band and get a different prefix, and the game would not
    /// find them.
    /// </remarks>
    private static string ProfileSourceOf(string stagedPath)
    {
        var match = ArchivePartName().Match(Path.GetFileName(stagedPath));
        if (!match.Success)
            return stagedPath;

        var first = Path.Combine(Path.GetDirectoryName(stagedPath) ?? string.Empty, match.Groups["base"].Value + ".pak");
        return File.Exists(first) && !LspkReader.HasHeader(stagedPath) ? first : stagedPath;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?<base>.+)_\d+\.pak$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ArchivePartName();

    /// <summary>The <c>&lt;band&gt;-&lt;order&gt;_</c> prefix Modular gives the paks it deploys.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^\d{2}-\d{3,}_")]
    internal static partial System.Text.RegularExpressions.Regex ExistingOrderPrefix();
}
