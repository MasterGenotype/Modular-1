using Modular.Sdk.Modules;

namespace Modular.Sdk.Compatibility;

/// <summary>
/// One mod present in a game, as far as is known about it. Mods Modular installed carry
/// their hub identity; mods put in place by hand may only have a name and their files.
/// </summary>
public sealed record InstalledMod
{
    /// <summary>
    /// Identifies the mod within one report. Hub mods use <c>backend:domain:id</c>; others
    /// use whatever their source names them by.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>Name fit to show.</summary>
    public required string Name { get; init; }

    /// <summary>Backend the mod came from, when known.</summary>
    public string? BackendId { get; init; }

    /// <summary>Game domain on that backend, for backends with per-game ids.</summary>
    public string? GameDomain { get; init; }

    /// <summary>The mod's id on that backend, when known.</summary>
    public string? ModId { get; init; }

    /// <summary>Installed versions. A mod installed from several files can have more than one.</summary>
    public IReadOnlyList<string> Versions { get; init; } = [];

    /// <summary>The mod's files, relative to the game folder, with forward slashes.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>Whether the mod can be looked up on a hub.</summary>
    public bool IsTraced => !string.IsNullOrEmpty(BackendId) && !string.IsNullOrEmpty(ModId);

    /// <summary>The key a hub mod gets: <c>backend:domain:id</c>, lower case.</summary>
    public static string HubKey(string backendId, string? gameDomain, string modId) =>
        $"{backendId}:{gameDomain}:{modId}".ToLowerInvariant();
}

/// <summary>
/// Why a mod counts as core: what breaks for other mods, or for the game, when it is
/// out of date.
/// </summary>
public enum CoreModRole
{
    /// <summary>Other installed mods list it as a requirement.</summary>
    Dependency,

    /// <summary>
    /// It loads other mods: a script compiler, a plugin loader, a mod framework the game
    /// starts through.
    /// </summary>
    Loader,

    /// <summary>
    /// It ships native code built against one game executable, so a game update can stop
    /// it loading or crash the game until it is rebuilt.
    /// </summary>
    NativeCode,

    /// <summary>Someone said so: a list kept by hand for mods the other signals cannot see.</summary>
    Declared
}

/// <summary>
/// One reason a signal found for treating a mod as core.
/// </summary>
/// <param name="ModKey">The <see cref="InstalledMod.Key"/> of the mod.</param>
/// <param name="Role">What the mod is to the game or to other mods.</param>
/// <param name="Signal">Id of the signal that found it.</param>
/// <param name="Reason">Why, in words fit to show.</param>
public sealed record CoreModEvidence(string ModKey, CoreModRole Role, string Signal, string Reason)
{
    /// <summary>Names of the installed mods that depend on this one, for <see cref="CoreModRole.Dependency"/>.</summary>
    public IReadOnlyList<string> Dependents { get; init; } = [];
}

/// <summary>
/// What a signal has to work with.
/// </summary>
/// <param name="GameKey">The game the mods belong to.</param>
/// <param name="Mods">Every mod present in the game.</param>
/// <param name="Module">The game's module, when one is known.</param>
public sealed record CoreModContext(string GameKey, IReadOnlyList<InstalledMod> Mods, IGameModule? Module);

/// <summary>
/// One way of telling which installed mods other mods, or the game, rely on.
/// </summary>
/// <remarks>
/// No single source is complete: authors leave requirements off their pages, loaders
/// are never listed as requirements because everyone has them, and some frameworks are
/// only known by reputation. Each signal covers one of these, and the classifier keeps
/// every reason any of them gives.
/// </remarks>
public interface ICoreModSignal
{
    /// <summary>Short id for the signal, shown beside the reasons it gives.</summary>
    string Id { get; }

    /// <summary>
    /// Finds core mods among <see cref="CoreModContext.Mods"/>.
    /// </summary>
    /// <returns>Evidence for each core mod found; a mod may get several.</returns>
    Task<IReadOnlyList<CoreModEvidence>> EvaluateAsync(CoreModContext context, CancellationToken ct = default);
}

/// <summary>
/// A file a game module knows marks a core mod, such as a plugin loader's DLL.
/// </summary>
/// <param name="PathPattern">
/// Path relative to the game folder, matched without regard to case. <c>*</c> matches
/// within one folder, <c>**</c> across folders.
/// </param>
/// <param name="Role">What a mod with such a file is.</param>
/// <param name="Reason">Why, in words fit to show.</param>
public sealed record CoreModRule(string PathPattern, CoreModRole Role, string Reason);

/// <summary>
/// Where the list of installed mods comes from.
/// </summary>
/// <remarks>
/// Modular's own install records are one source. A game modded by hand, or by another
/// manager, needs another, such as an inventory file listing which files belong to
/// which mod.
/// </remarks>
public interface IInstalledModSource
{
    /// <summary>Lists the mods present in the game.</summary>
    Task<IReadOnlyList<InstalledMod>> ListAsync(CancellationToken ct = default);
}

/// <summary>
/// A game update that mods have to catch up with.
/// </summary>
/// <param name="ReleasedAt">When the update came out. Mod files from before it predate it.</param>
/// <param name="Version">
/// The game version, such as <c>2.5</c>, when known. Used to notice files that say they
/// are for it.
/// </param>
public sealed record GameUpdateTarget(DateTimeOffset ReleasedAt, string? Version = null);

/// <summary>
/// Where a mod stands against a game update.
/// </summary>
public enum ReleaseVerdict
{
    /// <summary>The installed version was released on or after the update.</summary>
    OnTargetRelease,

    /// <summary>A newer release came out on or after the update and is not installed.</summary>
    UpdateAvailable,

    /// <summary>Nothing has been released since the update. The mod may still work.</summary>
    NoReleaseSinceTarget,

    /// <summary>The mod could not be looked up.</summary>
    Unknown,

    /// <summary>
    /// Nothing new since the update, but the mod has only files an update rarely affects,
    /// such as textures, so it likely needs no release for it.
    /// </summary>
    NotAffected
}

/// <summary>
/// What a release check found for one mod.
/// </summary>
/// <param name="Verdict">Where the mod stands.</param>
public sealed record ModReleaseStatus(ReleaseVerdict Verdict)
{
    /// <summary>Version of the newest release, when the hub gives one.</summary>
    public string? LatestVersion { get; init; }

    /// <summary>When the newest release was uploaded.</summary>
    public DateTimeOffset? LatestUploadedAt { get; init; }

    /// <summary>
    /// Whether a release since the update names the update's version. One that does not
    /// may be an unrelated fix, worth a look before trusting it.
    /// </summary>
    public bool MentionsTargetVersion { get; init; }

    /// <summary>Anything else worth showing, such as why the lookup failed.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Tells whether a mod has a release for a game update.
/// </summary>
public interface IModReleaseCheck
{
    /// <summary>Whether this check can look the mod up.</summary>
    bool CanCheck(InstalledMod mod);

    /// <summary>Looks up the mod's releases against the update.</summary>
    Task<ModReleaseStatus> CheckAsync(InstalledMod mod, GameUpdateTarget target, CancellationToken ct = default);
}
