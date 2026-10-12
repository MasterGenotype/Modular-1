using Modular.Sdk.Installers;

namespace Modular.Sdk.Modules;

/// <summary>
/// A self-contained backend for one game (or one family of games).
/// </summary>
/// <remarks>
/// <para>
/// A module answers four questions the install pipeline cannot answer generically:
/// which directory is this game, where do its mods belong, which installers know
/// how to read its archives, and what order does its engine load them in.
/// </para>
/// <para>
/// Only the first four members are required. Every hook below them has a default
/// no-op implementation, so a module can start as pure detection plus installers
/// and grow per-game behaviour later without breaking the contract.
/// </para>
/// </remarks>
public interface IGameModule
{
    /// <summary>
    /// Stable unique identifier for this module, such as <c>cyberpunk2077</c>.
    /// </summary>
    string ModuleId { get; }

    /// <summary>
    /// Human-readable name, for <c>modules list</c> and log output.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Identifiers this module answers to: NexusMods slugs, Steam AppIDs rendered
    /// as strings, and GameBanana game ids. Matched case-insensitively.
    /// </summary>
    IReadOnlyList<string> GameIds { get; }

    /// <summary>
    /// Steam Application IDs this module claims.
    /// </summary>
    IReadOnlyList<int> SteamAppIds { get; }

    /// <summary>
    /// Decides whether the given directory is this module's game, based on anchor
    /// files and directories.
    /// </summary>
    /// <param name="installPath">Candidate install directory.</param>
    /// <returns>
    /// A match with confidence and evidence, or <c>null</c> when this module does
    /// not claim the directory.
    /// </returns>
    /// <remarks>
    /// Anchors must be specific enough to reject sibling games that share a layout.
    /// FF7 Remake and FF7 Rebirth both use an <c>End/</c> project directory, so the
    /// executable name is what actually discriminates.
    /// </remarks>
    GameModuleMatch? Detect(string installPath);

    /// <summary>
    /// Resolves the named deployment roots for a specific installation.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <returns>Named roots, including the SDK standard roots.</returns>
    GameModPaths ResolveModPaths(GameInstallation installation);

    /// <summary>
    /// The installers this module contributes as candidates for its game.
    /// </summary>
    /// <returns>Installers, which may be empty when only universal ones apply.</returns>
    IReadOnlyList<IModInstaller> GetInstallers();

    /// <summary>
    /// Rewrites a staged file's destination, so a module can rename a file to
    /// encode load order or route it to a non-game root.
    /// </summary>
    /// <param name="context">The staged file plus its ordering information.</param>
    /// <returns>Where the file should actually be deployed.</returns>
    /// <remarks>The default is the identity mapping.</remarks>
    DeployTarget MapDeployPath(DeployPathContext context) =>
        new(context.RootName, context.RelativePath);

    /// <summary>
    /// Writes the engine's own load-order manifest, if it has one.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <param name="mods">Deployed mods in Modular order, where later wins.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the manifest has been written.</returns>
    /// <remarks>
    /// Modular's single user-facing rule is "later in the order list wins". A module
    /// translates that into the engine's own convention here, which may mean writing
    /// the list in reverse when the engine's first entry wins.
    /// </remarks>
    Task WriteLoadOrderAsync(
        GameInstallation installation,
        IReadOnlyList<DeployedMod> mods,
        CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Describes the order the engine will actually apply the deployed mods in.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <param name="mods">Deployed mods in Modular order, where later wins.</param>
    /// <returns>
    /// The engine's own ordering and the rule behind it, or <c>null</c> when the engine has
    /// no ordering worth showing separately.
    /// </returns>
    /// <remarks>
    /// The read-only counterpart to <see cref="WriteLoadOrderAsync"/>, so the translation
    /// from Modular's single rule into the engine's convention is visible rather than
    /// implicit. Cyberpunk is the case that needs it: the first legacy archive loaded wins,
    /// so its manifest is Modular's order reversed.
    /// </remarks>
    EngineOrderDescription? DescribeEngineOrder(
        GameInstallation installation,
        IReadOnlyList<DeployedMod> mods) => null;

    /// <summary>
    /// Finds dependencies that mods in the game's mod folders declare but nothing satisfies.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <returns>
    /// One entry per declaring mod and missing dependency. Empty for engines whose mods
    /// carry no dependency metadata.
    /// </returns>
    /// <remarks>
    /// Reads what is on disk, so it covers mods Modular did not install as well. It only
    /// names what is missing: the engine's identifier is not one any mod site knows, so
    /// finding a download is the caller's job.
    /// </remarks>
    IReadOnlyList<UnmetDependency> FindUnmetDependencies(GameInstallation installation) =>
        Array.Empty<UnmetDependency>();

    /// <summary>
    /// Finds mods in the game's mod folders that declare they conflict with another mod
    /// that is also present.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <returns>One entry per declared conflict; empty when the engine has no such metadata.</returns>
    IReadOnlyList<DeclaredConflict> FindDeclaredConflicts(GameInstallation installation) =>
        Array.Empty<DeclaredConflict>();

    /// <summary>
    /// Names the mods present in the game's mod folders, whoever installed them.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <returns>
    /// Mod names as the mods' own metadata gives them; empty when the engine's mods do not
    /// carry names.
    /// </returns>
    /// <remarks>
    /// Lets a requirement be recognised as met when the mod was installed by another
    /// manager, which leaves nothing in Modular's own records.
    /// </remarks>
    IReadOnlyList<string> ListPresentModNames(GameInstallation installation) =>
        Array.Empty<string>();

    /// <summary>
    /// Checks a staged mod's layout before it is deployed.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <param name="stagedMod">The staged mod.</param>
    /// <returns>
    /// Diagnostics. Any <see cref="DiagnosticSeverity.Error"/> aborts the install
    /// before it registers or deploys; warnings go into the install report.
    /// </returns>
    IReadOnlyList<ModDiagnostic> ValidateStaged(
        GameInstallation installation,
        StagedMod stagedMod) => Array.Empty<ModDiagnostic>();

    /// <summary>
    /// Glob patterns for files the game or its tools generate at runtime.
    /// </summary>
    /// <returns>Root-relative globs, such as <c>r6/cache/**</c>.</returns>
    /// <remarks>
    /// These are excluded from vanilla backup and restore, excluded from conflict
    /// reports, and never purged, so purging a deployment cannot destroy user state
    /// or clobber a tool's output.
    /// </remarks>
    IReadOnlyList<string> GetGeneratedPaths() => Array.Empty<string>();

    /// <summary>
    /// Reports what the user must do for the deployed set to actually load.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <param name="mods">The deployed mods.</param>
    /// <returns>Launch arguments, Wine DLL overrides and pending tool actions.</returns>
    LaunchRequirements GetLaunchRequirements(
        GameInstallation installation,
        IReadOnlyList<DeployedMod> mods) => LaunchRequirements.None;

    /// <summary>
    /// A declarative table mapping package types to destinations.
    /// </summary>
    /// <returns>Routes, or an empty list when the module routes through an analyzer.</returns>
    /// <remarks>
    /// Lets a future module be defined by data alone, routed by a generic
    /// table-driven installer instead of a bespoke archive analyzer.
    /// </remarks>
    IReadOnlyList<PackageRoute> GetPackageRoutes() => Array.Empty<PackageRoute>();

    /// <summary>
    /// Restrictions on how files may be placed for this game.
    /// </summary>
    /// <returns>Constraints, or an empty list when any deployment method is safe.</returns>
    /// <remarks>
    /// Constraints override the user's chosen deployment method file by file and
    /// are listed in the deploy report.
    /// </remarks>
    IReadOnlyList<DeployConstraint> GetDeployConstraints() => Array.Empty<DeployConstraint>();
}
