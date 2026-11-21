using Modular.Core.Archives;
using Modular.Core.Installers.Enshrouded;
using Modular.Sdk;
using Modular.Sdk.Archives;
using Modular.Sdk.Compatibility;
using Modular.Sdk.Installers;
using Modular.Sdk.Modules;

namespace Modular.Core.Modules;

/// <summary>
/// Game module for Enshrouded (Steam AppID 1203620).
/// </summary>
/// <remarks>
/// <para>
/// Enshrouded's mods run through EML (Enshrouded Mod Loader). Modular installs the mods
/// to <c>mods/&lt;id&gt;/</c> and installs EML itself, as an ordinary mod, the first time
/// any Enshrouded mod is installed (<see cref="GetRequiredFrameworks"/>).
/// </para>
/// <para>
/// EML rewrites <c>enshrouded.kfc</c> from its own backup on every launch, so the game
/// folder's data index is EML's to manage while EML is installed; once it is removed,
/// <see cref="AfterDeploymentChanged"/> restores the original.
/// </para>
/// </remarks>
public sealed class EnshroudedGameModule : GameModuleBase
{
    /// <summary>EML, pinned to the release that supports Enshrouded 1.0.</summary>
    public static readonly RequiredFramework Eml = new(
        Id: "eml",
        DisplayName: "Enshrouded Mod Loader (EML)",
        Version: "0.2.0",
        DownloadUrl: "https://github.com/Brabb3l/kfc-parser/releases/download/v0.2.0/dbghelp.dll",
        Sha256: "038b7a1787026007a68c82a6adeb8e79c2e644f936d4cd00f8b0db206a2349fa",
        FileName: "dbghelp.dll",
        PresencePath: "dbghelp.dll");

    private readonly IArchiveReaderFactory _archiveReaderFactory;
    private readonly RequiredFramework _loader;

    /// <summary>
    /// Creates the module.
    /// </summary>
    /// <param name="archiveReaderFactory">Shared archive reader factory.</param>
    /// <param name="loader">The loader to install; <see cref="Eml"/> unless a test says otherwise.</param>
    public EnshroudedGameModule(
        IArchiveReaderFactory? archiveReaderFactory = null,
        RequiredFramework? loader = null)
    {
        _archiveReaderFactory = archiveReaderFactory ?? new ArchiveReaderFactory();
        _loader = loader ?? Eml;
    }

    /// <inheritdoc />
    public override string ModuleId => "enshrouded";

    /// <inheritdoc />
    public override string DisplayName => "Enshrouded";

    /// <inheritdoc />
    public override IReadOnlyList<string> GameIds { get; } = [Sdk.GameIds.Enshrouded, "1203620"];

    /// <inheritdoc />
    public override IReadOnlyList<int> SteamAppIds { get; } = [1203620];

    /// <inheritdoc />
    protected override IReadOnlyList<string> PrimaryAnchors { get; } =
        ["enshrouded.exe", EnshroudedKfc.IndexFile];

    /// <inheritdoc />
    protected override IReadOnlyList<string> SupportingAnchors { get; } =
        ["enshrouded.kfc_resources"];

    /// <inheritdoc />
    public override IReadOnlyList<IModInstaller> GetInstallers() =>
        [new EnshroudedModInstaller(_archiveReaderFactory)];

    /// <inheritdoc />
    public override IReadOnlyList<RequiredFramework> GetRequiredFrameworks() => [_loader];

    /// <summary>
    /// Under Proton a proxy DLL beside the executable is ignored unless it is overridden
    /// native-first, so every deployed loader DLL needs one.
    /// </summary>
    public override LaunchRequirements GetLaunchRequirements(
        GameInstallation installation,
        IReadOnlyList<DeployedMod> mods)
    {
        ArgumentNullException.ThrowIfNull(mods);

        var overrides = mods
            .SelectMany(m => m.DeployedFiles)
            .Where(f => string.Equals(f.RootName, ModRoots.Game, StringComparison.OrdinalIgnoreCase) &&
                        !f.RelativePath.Replace('\\', '/').Contains('/') &&
                        EnshroudedArchiveAnalyzer.LoaderDllNames.Contains(f.RelativePath))
            .Select(f => Path.GetFileNameWithoutExtension(f.RelativePath).ToLowerInvariant())
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        return overrides.Count == 0
            ? LaunchRequirements.None
            : new LaunchRequirements { WineDllOverrides = overrides };
    }

    /// <summary>
    /// What EML writes as it runs: its settings, its cache, exports, logs and the backup
    /// of the data index. The index itself is left out on purpose: a mod shipping a
    /// replacement <c>enshrouded.kfc</c> must still get a vanilla backup.
    /// </summary>
    public override IReadOnlyList<string> GetGeneratedPaths() =>
    [
        "eml.json",
        ".cache/**",
        "export/**",
        "logs/**",
        "*.log",
        EnshroudedKfc.BackupFile
    ];

    /// <inheritdoc />
    public override IReadOnlyList<CoreModRule> GetCoreModRules() =>
    [
        new("dbghelp.dll", CoreModRole.Loader, "Enshrouded Mod Loader (EML)"),
        new("dinput8.dll", CoreModRole.Loader, "Enshrouded Mod Loader (EML), fallback proxy"),
        new("winmm.dll", CoreModRole.Loader, "ShroudForge mod loader"),
        new("dbgcore.dll", CoreModRole.Loader, "Greeneye native mod loader"),
        new("powrprof.dll", CoreModRole.NativeCode, "Native plugin loader (Shrouded Engine Tweaks or Combat Rebalanced)")
    ];

    /// <summary>
    /// ShroudForge's in-game menu saves each mod's on/off switch into the mod's own
    /// <c>extended.mod.json</c>; a hardlink would carry that write back into Modular's
    /// staged copy.
    /// </summary>
    public override IReadOnlyList<DeployConstraint> GetDeployConstraints() =>
    [
        new("mods/*/extended.mod.json", ForceCopy: true, DisallowSymlink: true,
            "ShroudForge writes mod settings into this file")
    ];

    /// <inheritdoc />
    public override IReadOnlyList<string> AfterDeploymentChanged(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);

        return EnshroudedKfc.RestoreIfLoaderGone(installation.InstallPath) is { } line
            ? [line]
            : [];
    }

    /// <summary>
    /// Checks each staged <c>mod.json</c> the way EML will read it. EML stops loading
    /// every mod when one manifest is unreadable or two share an id, so either is an error
    /// that keeps the mod out of the game folder.
    /// </summary>
    public override IReadOnlyList<ModDiagnostic> ValidateStaged(
        GameInstallation installation,
        StagedMod stagedMod)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(stagedMod);

        var diagnostics = new List<ModDiagnostic>();

        foreach (var file in stagedMod.Files)
        {
            var path = file.RelativePath.Replace('\\', '/');
            var parts = path.Split('/');

            if (parts.Length != 3 ||
                !string.Equals(parts[0], EnshroudedArchiveAnalyzer.ModsFolder, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(parts[2], "extended.mod.json", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(ModDiagnostic.Warning(
                    "enshrouded.shroudforge_mod",
                    "This mod is packaged for ShroudForge (it has extended.mod.json). EML runs its " +
                    "mod.json part; settings only ShroudForge's menu provides are not available.",
                    path));
                continue;
            }

            if (!string.Equals(parts[2], EnshroudedArchiveAnalyzer.ManifestName, StringComparison.OrdinalIgnoreCase))
                continue;

            var stagedPath = Path.Combine(stagedMod.StagingPath, file.RootName, path);
            if (!File.Exists(stagedPath))
                continue;

            var json = File.ReadAllText(stagedPath);

            foreach (var problem in EnshroudedArchiveAnalyzer.FindEmlManifestProblems(json))
            {
                diagnostics.Add(ModDiagnostic.Error(
                    "enshrouded.bad_manifest",
                    $"{problem}. EML would refuse to start, and no Enshrouded mod would load.",
                    path));
            }

            var id = EnshroudedArchiveAnalyzer.ParseManifest(json)?.Id;
            if (id != null && FindOtherFolderWithId(installation.InstallPath, parts[1], id) is { } other)
            {
                diagnostics.Add(ModDiagnostic.Error(
                    "enshrouded.duplicate_id",
                    $"mods/{other} already has the mod id '{id}'. EML refuses to start when two mods " +
                    "share an id; remove that one first.",
                    path));
            }
        }

        return diagnostics;
    }

    /// <summary>
    /// Finds another folder in the game's <c>mods/</c> whose manifest has the given id.
    /// </summary>
    private static string? FindOtherFolderWithId(string gameDirectory, string folder, string id)
    {
        var mods = Path.Combine(gameDirectory, EnshroudedArchiveAnalyzer.ModsFolder);
        if (!Directory.Exists(mods))
            return null;

        foreach (var directory in Directory.EnumerateDirectories(mods))
        {
            var name = Path.GetFileName(directory);
            if (string.Equals(name, folder, StringComparison.OrdinalIgnoreCase) || name.StartsWith('.'))
                continue;

            var manifest = Path.Combine(directory, EnshroudedArchiveAnalyzer.ManifestName);
            if (!File.Exists(manifest))
                continue;

            try
            {
                if (string.Equals(EnshroudedArchiveAnalyzer.ParseManifest(File.ReadAllText(manifest))?.Id, id, StringComparison.Ordinal))
                    return name;
            }
            catch (IOException)
            {
                // Unreadable: EML will report it itself.
            }
        }

        return null;
    }
}
