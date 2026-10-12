namespace Modular.Sdk.Modules;

/// <summary>
/// Result of a module's attempt to claim a game directory.
/// </summary>
public sealed class GameModuleMatch
{
    /// <summary>
    /// Confidence that this module is the correct one for the directory (0.0 - 1.0).
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// Anchor files and directories that justified the match, for logging and
    /// <c>modular detect</c> output.
    /// </summary>
    public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The install root the module anchored on. Normally the directory that was
    /// probed, but a module may report a parent when it was handed a subdirectory.
    /// </summary>
    public string? ResolvedInstallPath { get; init; }
}

/// <summary>
/// Which routing rule decided a file's destination.
/// </summary>
/// <remarks>
/// Recorded per operation so a mis-routed file can be traced back to the rule
/// that placed it. See the "destination = game x package type" design.
/// </remarks>
public enum RouteSource
{
    /// <summary>
    /// The archive already declared a recognised game-relative path, so the tree
    /// was deployed as-is. The strongest signal.
    /// </summary>
    Declared,

    /// <summary>
    /// An author- or loader-provided manifest decided the destination (REDmod
    /// <c>info.json</c>, FOMOD <c>ModuleConfig.xml</c>, loader descriptors).
    /// </summary>
    Metadata,

    /// <summary>
    /// A loose-file extension rule decided the destination. The weakest signal,
    /// and only applied to files no stronger rule claimed.
    /// </summary>
    Heuristic
}

/// <summary>
/// How the files of a package type are laid out beneath their destination.
/// </summary>
public enum PackageLayout
{
    /// <summary>Keep the archive's directory structure below the destination.</summary>
    Preserve,

    /// <summary>
    /// Place files directly in the destination with no intermediate folder.
    /// Inventing a <c>&lt;modname&gt;/</c> level breaks these (Cyberpunk
    /// <c>archive/pc/mod</c>, BG3 <c>Mods</c>).
    /// </summary>
    FlatFiles,

    /// <summary>Place the package in its own <c>&lt;modname&gt;/</c> folder.</summary>
    FolderPerMod,

    /// <summary>
    /// Like <see cref="FlatFiles"/>, but the file must keep its original name and
    /// sort after every order-prefixed file. Used by loader paks that rely on their
    /// own name to load last.
    /// </summary>
    PinnedLast
}

/// <summary>
/// A declarative routing rule: where files of a given package type belong.
/// </summary>
/// <remarks>
/// Returned by <see cref="IGameModule.GetPackageRoutes"/>. Lets a module describe
/// its destinations as data instead of as a bespoke archive analyzer.
/// </remarks>
/// <param name="PackageType">
/// Package type id this route applies to, such as <c>cp77.legacy_archive</c>,
/// <c>ue.pak_bundle</c> or <c>bg3.pak</c>.
/// </param>
/// <param name="Root">
/// Name of the deployment root, either a <see cref="ModRoots"/> constant or a
/// module-defined root.
/// </param>
/// <param name="SubPath">Path beneath the root, or an empty string for the root itself.</param>
/// <param name="Layout">How files are laid out beneath <paramref name="SubPath"/>.</param>
public readonly record struct PackageRoute(
    string PackageType,
    string Root,
    string SubPath,
    PackageLayout Layout);

/// <summary>
/// Input to <see cref="IGameModule.MapDeployPath"/>: one staged file, together
/// with the ordering information a module needs to rename or reroute it.
/// </summary>
/// <param name="RootName">Name of the root the file was staged under.</param>
/// <param name="RelativePath">Path relative to that root, using forward slashes.</param>
/// <param name="OrderIndex">Zero-based position of the owning mod in the load order.</param>
/// <param name="EnabledModCount">Number of enabled mods, so prefixes can be zero-padded consistently.</param>
/// <param name="ModId">Identifier of the mod that staged this file.</param>
/// <param name="PackageType">Package type label from the install plan, when known.</param>
/// <param name="StagedFilePath">
/// Absolute path of the staged file, for a module that has to look inside it to decide
/// where it goes. Null when the caller has no file to point at.
/// </param>
public readonly record struct DeployPathContext(
    string RootName,
    string RelativePath,
    int OrderIndex,
    int EnabledModCount,
    string ModId,
    string? PackageType = null,
    string? StagedFilePath = null);

/// <summary>
/// Output of <see cref="IGameModule.MapDeployPath"/>: the root and path a staged
/// file should actually be deployed to.
/// </summary>
/// <param name="RootName">Name of the destination root.</param>
/// <param name="RelativePath">Path relative to that root.</param>
/// <param name="PinnedLast">
/// When true, the file keeps its name and must be ordered after all order-prefixed
/// files. Used for loader paks that rely on their own name to sort last.
/// </param>
public readonly record struct DeployTarget(
    string RootName,
    string RelativePath,
    bool PinnedLast = false);

/// <summary>
/// A mod that has been staged and is ready to be validated or deployed.
/// </summary>
public sealed class StagedMod
{
    /// <summary>Identifier of the mod.</summary>
    public string ModId { get; init; } = string.Empty;

    /// <summary>Absolute path to the mod's staging directory.</summary>
    public string StagingPath { get; init; } = string.Empty;

    /// <summary>
    /// Staged files as <c>(root name, path relative to that root)</c> pairs,
    /// already normalised so every consumer sees game-root-relative paths.
    /// </summary>
    public IReadOnlyList<StagedFile> Files { get; init; } = Array.Empty<StagedFile>();
}

/// <summary>
/// One staged file, normalised to a named root and a root-relative path.
/// </summary>
/// <param name="RootName">Name of the root this file belongs under.</param>
/// <param name="RelativePath">Path relative to that root, using forward slashes.</param>
/// <param name="PackageType">Package type label, when the installer supplied one.</param>
/// <param name="RouteSource">Which rule decided this file's destination.</param>
public readonly record struct StagedFile(
    string RootName,
    string RelativePath,
    string? PackageType = null,
    RouteSource RouteSource = RouteSource.Heuristic);

/// <summary>
/// A mod that has been deployed into the game, in load order.
/// </summary>
public sealed class DeployedMod
{
    /// <summary>Identifier of the mod.</summary>
    public string ModId { get; init; } = string.Empty;

    /// <summary>Human-readable mod name, when known.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Zero-based position in the load order. Later wins.</summary>
    public int OrderIndex { get; init; }

    /// <summary>Whether the mod is currently enabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Files this mod contributed to the deployment, as deployed
    /// (that is, after <see cref="IGameModule.MapDeployPath"/>).
    /// </summary>
    public IReadOnlyList<StagedFile> DeployedFiles { get; init; } = Array.Empty<StagedFile>();
}

/// <summary>
/// How an engine orders the mods Modular deployed.
/// </summary>
/// <param name="Rule">
/// The rule in one phrase, such as "alphabetical, first loaded wins" — shown next to
/// Modular's order so the translation is legible.
/// </param>
/// <param name="Entries">
/// The engine's order, first applied to last. Entries are whatever the engine actually
/// sorts: filenames for a filename-ordered engine, mod names for a manifest-driven one.
/// </param>
/// <param name="Mechanism">
/// How the order is expressed, such as a manifest path or "filename prefixes".
/// </param>
public readonly record struct EngineOrderDescription(
    string Rule,
    IReadOnlyList<string> Entries,
    string? Mechanism = null);

/// <summary>
/// A dependency a mod on disk declares in its own metadata, for which nothing is installed.
/// </summary>
/// <param name="RequiredBy">Name of the mod that declares the dependency.</param>
/// <param name="RequiredByFile">
/// File name of the mod that declares it, as it sits in the game's mod folder. Lets the
/// gap be traced back to the mod Modular installed; <c>null</c> when there is no single file.
/// </param>
/// <param name="Name">Name of the missing mod, as the declaring mod spells it.</param>
/// <param name="Identifier">The engine's own identifier for the missing mod.</param>
public sealed record UnmetDependency(
    string RequiredBy,
    string? RequiredByFile,
    string Name,
    string Identifier);

/// <summary>
/// Two mods on disk of which one declares, in its own metadata, that it cannot be loaded
/// with the other.
/// </summary>
/// <param name="DeclaredBy">Name of the mod that declares the conflict.</param>
/// <param name="DeclaredByFile">File name of that mod, when there is a single file.</param>
/// <param name="ConflictsWith">Name of the mod it conflicts with, which is also present.</param>
/// <param name="Identifier">The engine's own identifier for the conflicting mod.</param>
public sealed record DeclaredConflict(
    string DeclaredBy,
    string? DeclaredByFile,
    string ConflictsWith,
    string Identifier);

/// <summary>
/// Severity of a <see cref="ModDiagnostic"/>.
/// </summary>
public enum DiagnosticSeverity
{
    /// <summary>Informational note; shown but never blocking.</summary>
    Info,

    /// <summary>Something is probably wrong, but the install may proceed.</summary>
    Warning,

    /// <summary>The layout is invalid; the install must not be deployed.</summary>
    Error
}

/// <summary>
/// A layout problem found by <see cref="IGameModule.ValidateStaged"/>.
/// </summary>
public sealed class ModDiagnostic
{
    /// <summary>How serious the finding is.</summary>
    public DiagnosticSeverity Severity { get; init; } = DiagnosticSeverity.Warning;

    /// <summary>Stable machine-readable code, for example <c>cp77.nested_archive</c>.</summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>Human-readable explanation.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>The staged file the finding concerns, when it is file-specific.</summary>
    public string? RelativePath { get; init; }

    /// <summary>Suggested fix, when the module can offer one.</summary>
    public string? Remedy { get; init; }

    /// <summary>Creates an error-severity diagnostic.</summary>
    /// <param name="code">Stable machine-readable code.</param>
    /// <param name="message">Human-readable explanation.</param>
    /// <param name="relativePath">The staged file concerned, if any.</param>
    /// <returns>The diagnostic.</returns>
    public static ModDiagnostic Error(string code, string message, string? relativePath = null) =>
        new() { Severity = DiagnosticSeverity.Error, Code = code, Message = message, RelativePath = relativePath };

    /// <summary>Creates a warning-severity diagnostic.</summary>
    /// <param name="code">Stable machine-readable code.</param>
    /// <param name="message">Human-readable explanation.</param>
    /// <param name="relativePath">The staged file concerned, if any.</param>
    /// <returns>The diagnostic.</returns>
    public static ModDiagnostic Warning(string code, string message, string? relativePath = null) =>
        new() { Severity = DiagnosticSeverity.Warning, Code = code, Message = message, RelativePath = relativePath };
}

/// <summary>
/// What the user must do for a deployed mod set to actually load.
/// </summary>
/// <remarks>
/// Modular reports these; it never edits Steam's launch options itself.
/// </remarks>
public sealed class LaunchRequirements
{
    /// <summary>A requirement set with nothing in it.</summary>
    public static LaunchRequirements None { get; } = new();

    /// <summary>
    /// Launch arguments the game needs, such as <c>-modded</c> for Cyberpunk REDmods.
    /// </summary>
    public IReadOnlyList<string> LaunchArguments { get; init; } = Array.Empty<string>();

    /// <summary>
    /// DLL names that need a native-first override under Proton, such as
    /// <c>winmm</c> or <c>winhttp</c>. Rendered as
    /// <c>WINEDLLOVERRIDES="name=n,b" %command%</c>.
    /// </summary>
    public IReadOnlyList<string> WineDllOverrides { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Tools that must run after deployment, such as <c>redMod.exe deploy</c>.
    /// </summary>
    public IReadOnlyList<PostDeployAction> PostDeployActions { get; init; } = Array.Empty<PostDeployAction>();

    /// <summary>Whether this requirement set has anything to report.</summary>
    public bool IsEmpty =>
        LaunchArguments.Count == 0 &&
        WineDllOverrides.Count == 0 &&
        PostDeployActions.Count == 0;
}

/// <summary>
/// A tool invocation that should run after a successful deployment.
/// </summary>
/// <param name="Description">What the action does, for the report.</param>
/// <param name="Executable">Absolute path to the executable.</param>
/// <param name="Arguments">Arguments, already ordered.</param>
/// <param name="AutoRunnable">
/// Whether Modular may run this itself. False when it would need Proton, in which
/// case the command is printed instead.
/// </param>
public readonly record struct PostDeployAction(
    string Description,
    string Executable,
    IReadOnlyList<string> Arguments,
    bool AutoRunnable);

/// <summary>
/// A module-imposed restriction on how files may be placed.
/// </summary>
/// <remarks>
/// Lets a game whose loader or anti-cheat misbehaves with links force copies for
/// specific paths, without any change to core deployment code.
/// </remarks>
/// <param name="PathGlob">
/// Glob matched against the root-relative deploy path, or <c>*</c> for the whole game.
/// </param>
/// <param name="ForceCopy">When true, matching files must be copied rather than linked.</param>
/// <param name="DisallowSymlink">When true, matching files must never be symlinked.</param>
/// <param name="Reason">Why the constraint exists, for the deploy report.</param>
public readonly record struct DeployConstraint(
    string PathGlob,
    bool ForceCopy,
    bool DisallowSymlink,
    string Reason);
