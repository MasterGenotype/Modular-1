using Modular.Sdk.Installers;
using Modular.Sdk.Modules;

namespace Modular.Core.Modules;

/// <summary>
/// Base class for built-in game modules. Implements anchor-based detection so
/// each module only has to declare which files and directories identify its game.
/// </summary>
/// <remarks>
/// <para>
/// Detection has three kinds of anchor:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="PrimaryAnchors"/> — every one must be present. These carry the match.
/// </description></item>
/// <item><description>
/// <see cref="SupportingAnchors"/> — each present anchor nudges confidence up.
/// </description></item>
/// <item><description>
/// <see cref="RejectingAnchors"/> — any present anchor vetoes the match outright.
/// This is what stops a sibling game that shares a directory layout from being
/// mistaken for this one.
/// </description></item>
/// </list>
/// </remarks>
public abstract class GameModuleBase : IGameModule
{
    /// <inheritdoc />
    public abstract string ModuleId { get; }

    /// <inheritdoc />
    public abstract string DisplayName { get; }

    /// <inheritdoc />
    public abstract IReadOnlyList<string> GameIds { get; }

    /// <inheritdoc />
    public abstract IReadOnlyList<int> SteamAppIds { get; }

    /// <summary>
    /// Paths, relative to the install root, that must <em>all</em> exist for this
    /// module to claim a directory. Either files or directories.
    /// </summary>
    protected abstract IReadOnlyList<string> PrimaryAnchors { get; }

    /// <summary>
    /// Paths that raise confidence when present but are not required.
    /// </summary>
    protected virtual IReadOnlyList<string> SupportingAnchors => Array.Empty<string>();

    /// <summary>
    /// Paths whose presence proves this is <em>not</em> this module's game.
    /// </summary>
    /// <remarks>
    /// Used to separate games that share a directory layout, such as FF7 Remake
    /// and FF7 Rebirth, which both use an <c>End/</c> project directory.
    /// </remarks>
    protected virtual IReadOnlyList<string> RejectingAnchors => Array.Empty<string>();

    /// <summary>
    /// Confidence reported when every primary anchor is present, before any
    /// supporting-anchor bonus.
    /// </summary>
    protected virtual double BaseConfidence => 0.95;

    /// <summary>
    /// Extra named deployment roots this module contributes, beyond the SDK
    /// standard roots.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <returns>Root name to absolute path, or <c>null</c> for none.</returns>
    protected virtual IReadOnlyDictionary<string, string>? GetExtraRoots(
        GameInstallation installation) => null;

    /// <inheritdoc />
    public virtual GameModuleMatch? Detect(string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
            return null;

        // A rejecting anchor vetoes the match, however strong the evidence is.
        foreach (var reject in RejectingAnchors)
        {
            if (PathExists(installPath, reject))
                return null;
        }

        var evidence = new List<string>();

        foreach (var anchor in PrimaryAnchors)
        {
            if (!PathExists(installPath, anchor))
                return null;

            evidence.Add(anchor);
        }

        if (evidence.Count == 0)
            return null;

        var confidence = BaseConfidence;

        foreach (var supporting in SupportingAnchors)
        {
            if (!PathExists(installPath, supporting))
                continue;

            evidence.Add(supporting);
            confidence += 0.01;
        }

        return new GameModuleMatch
        {
            Confidence = Math.Min(confidence, 1.0),
            Evidence = evidence,
            ResolvedInstallPath = installPath
        };
    }

    /// <inheritdoc />
    public virtual GameModPaths ResolveModPaths(GameInstallation installation) =>
        GameModPaths.ForInstallation(installation, GetExtraRoots(installation));

    /// <inheritdoc />
    public abstract IReadOnlyList<IModInstaller> GetInstallers();

    // ── Optional hooks ──────────────────────────────────────────────────────
    //
    // These mirror IGameModule's default implementations as virtual members. A default
    // interface member cannot be overridden from a subclass: interface mapping is fixed at
    // the class that declares it implements the interface, which is this one. Without these
    // passthroughs a hook written in a derived module would compile as a new method and
    // never be called.

    /// <inheritdoc />
    public virtual DeployTarget MapDeployPath(DeployPathContext context) =>
        new(context.RootName, context.RelativePath);

    /// <inheritdoc />
    public virtual Task WriteLoadOrderAsync(
        GameInstallation installation,
        IReadOnlyList<DeployedMod> mods,
        CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public virtual EngineOrderDescription? DescribeEngineOrder(
        GameInstallation installation,
        IReadOnlyList<DeployedMod> mods) => null;

    /// <inheritdoc />
    public virtual IReadOnlyList<UnmetDependency> FindUnmetDependencies(
        GameInstallation installation) => Array.Empty<UnmetDependency>();

    /// <inheritdoc />
    public virtual IReadOnlyList<DeclaredConflict> FindDeclaredConflicts(
        GameInstallation installation) => Array.Empty<DeclaredConflict>();

    /// <inheritdoc />
    public virtual IReadOnlyList<string> ListPresentModNames(
        GameInstallation installation) => Array.Empty<string>();

    /// <inheritdoc />
    public virtual IReadOnlyList<ModDiagnostic> ValidateStaged(
        GameInstallation installation,
        StagedMod stagedMod) => Array.Empty<ModDiagnostic>();

    /// <inheritdoc />
    public virtual IReadOnlyList<string> GetGeneratedPaths() => Array.Empty<string>();

    /// <inheritdoc />
    public virtual LaunchRequirements GetLaunchRequirements(
        GameInstallation installation,
        IReadOnlyList<DeployedMod> mods) => LaunchRequirements.None;

    /// <inheritdoc />
    public virtual IReadOnlyList<PackageRoute> GetPackageRoutes() => Array.Empty<PackageRoute>();

    /// <inheritdoc />
    public virtual IReadOnlyList<DeployConstraint> GetDeployConstraints() =>
        Array.Empty<DeployConstraint>();

    /// <inheritdoc />
    public virtual IReadOnlyList<Sdk.Compatibility.CoreModRule> GetCoreModRules() =>
        Array.Empty<Sdk.Compatibility.CoreModRule>();

    /// <inheritdoc />
    public virtual IReadOnlyList<string>? GetUpdateSensitivePaths() => null;

    /// <summary>
    /// Tests whether a path relative to the install root exists, as either a file
    /// or a directory. Matching is case-insensitive even on case-sensitive
    /// filesystems, because Proton-hosted Windows games are routinely installed
    /// with unpredictable casing.
    /// </summary>
    /// <param name="installPath">The install root.</param>
    /// <param name="relativePath">Path relative to that root, using forward slashes.</param>
    /// <returns>True when the path exists.</returns>
    protected static bool PathExists(string installPath, string relativePath)
    {
        var combined = Path.Combine(
            installPath,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        if (File.Exists(combined) || Directory.Exists(combined))
            return true;

        return CaseInsensitiveResolve(installPath, relativePath) != null;
    }

    /// <summary>
    /// Resolves a relative path against an install root, ignoring case at every
    /// segment, and returns the real path on disk.
    /// </summary>
    /// <param name="installPath">The install root.</param>
    /// <param name="relativePath">Path relative to that root, using forward slashes.</param>
    /// <returns>The actual path, or <c>null</c> when no segment chain matches.</returns>
    protected static string? CaseInsensitiveResolve(string installPath, string relativePath)
    {
        var segments = relativePath.Split(
            ['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        var current = installPath;

        foreach (var segment in segments)
        {
            if (!Directory.Exists(current))
                return null;

            string? match;
            try
            {
                match = Directory
                    .EnumerateFileSystemEntries(current)
                    .FirstOrDefault(e => string.Equals(
                        Path.GetFileName(e), segment, StringComparison.OrdinalIgnoreCase));
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }

            if (match == null)
                return null;

            current = match;
        }

        return current;
    }
}
