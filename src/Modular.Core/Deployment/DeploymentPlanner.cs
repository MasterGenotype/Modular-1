using Microsoft.Extensions.Logging;
using Modular.Core.Dependencies;
using Modular.Core.Installers;
using Modular.Sdk.Modules;

namespace Modular.Core.Deployment;

/// <summary>
/// Turns an ordered list of staged mods into the exact set of files to place, resolving
/// which mod wins each destination.
/// </summary>
/// <remarks>
/// <para>
/// Two different orderings meet here. <em>File priority</em> decides which physical file
/// is deployed when two mods ship the same relative path — that is this class's job, and
/// the rule is "later in the order wins". <em>Engine load order</em> decides which of two
/// different files the game applies to the same asset — that belongs to the module, which
/// translates Modular's order into its engine's own convention.
/// </para>
/// <para>
/// A module's <see cref="IGameModule.MapDeployPath"/> runs before the winner is chosen, so
/// a module that encodes order into filenames (a numbered pak prefix, say) produces
/// distinct destinations and no longer collides with itself.
/// </para>
/// </remarks>
public sealed class DeploymentPlanner
{
    private readonly ILogger<DeploymentPlanner>? _logger;

    /// <summary>
    /// Creates a planner.
    /// </summary>
    /// <param name="logger">Optional logger.</param>
    public DeploymentPlanner(ILogger<DeploymentPlanner>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Plans a deployment.
    /// </summary>
    /// <param name="installation">The resolved game installation.</param>
    /// <param name="module">The module owning the game.</param>
    /// <param name="modPaths">Resolved named roots.</param>
    /// <param name="mods">Enabled mods in load order, later winning.</param>
    /// <param name="current">The current deployment manifest, for unmanaged-file reporting.</param>
    /// <returns>The plan: winning files, overlaps, and notes for the report.</returns>
    public DeploymentPlan Plan(
        GameInstallation installation,
        IGameModule module,
        GameModPaths modPaths,
        IReadOnlyList<DeploymentModInput> mods,
        DeploymentManifest? current = null)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(modPaths);
        ArgumentNullException.ThrowIfNull(mods);

        var plan = new DeploymentPlan();
        var generatedGlobs = SafeGeneratedPaths(module);

        // Winner per destination. Insertion order is preserved, so a stable plan comes out
        // of a stable input order.
        var winners = new Dictionary<DeployKey, PlannedFile>();
        var conflictIndex = new FileConflictIndex();

        // Paks a module pinned to load last are placed after every ordered file.
        var pinned = new List<PlannedFile>();

        foreach (var mod in mods)
        {
            foreach (var staged in mod.Files)
            {
                var target = SafeMapDeployPath(module, new DeployPathContext(
                    staged.RootName,
                    staged.RelativePath,
                    mod.OrderIndex,
                    mods.Count,
                    mod.ModId,
                    staged.PackageType));

                var rootName = string.IsNullOrEmpty(target.RootName)
                    ? staged.RootName
                    : target.RootName;
                var relativePath = target.RelativePath.Replace('\\', '/').TrimStart('/');

                var rootPath = modPaths.GetRoot(rootName);
                if (rootPath == null)
                {
                    plan.Skipped.Add(new SkippedFile(
                        mod.ModId, rootName, relativePath,
                        $"deployment root '{rootName}' is not available for this installation"));
                    continue;
                }

                // A file the game or its tools generate is never Modular's to place.
                if (MatchesAnyGlob(relativePath, generatedGlobs))
                {
                    plan.Skipped.Add(new SkippedFile(
                        mod.ModId, rootName, relativePath,
                        "matches a generated path owned by the game or its tools"));
                    continue;
                }

                var planned = new PlannedFile
                {
                    ModId = mod.ModId,
                    RootName = rootName,
                    RelativePath = relativePath,
                    StagedPath = Path.Combine(
                        mod.StagingPath,
                        staged.RootName,
                        staged.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
                    DestinationPath = Path.Combine(
                        rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)),
                    PackageType = staged.PackageType,
                    OrderIndex = mod.OrderIndex,
                    PinnedLast = target.PinnedLast
                };

                if (target.PinnedLast)
                {
                    // A pinned file keeps its own name and must not be reordered into the
                    // numbered band, so it does not participate in last-writer-wins.
                    pinned.Add(planned);
                    continue;
                }

                var key = new DeployKey(rootName, relativePath);

                if (winners.TryGetValue(key, out var previous))
                {
                    // Later wins. The loser is recorded so the report can name it.
                    plan.Overlaps.Add(new DeploymentOverlap(
                        rootName, relativePath, previous.ModId, mod.ModId));

                    conflictIndex.RegisterFile(
                        $"{rootName}/{relativePath}", previous.ModId, previous.StagedPath, null);
                    conflictIndex.RegisterFile(
                        $"{rootName}/{relativePath}", mod.ModId, planned.StagedPath, null);
                }

                winners[key] = planned;
            }
        }

        plan.Files.AddRange(winners.Values);
        plan.Files.AddRange(pinned);
        plan.ConflictIndex = conflictIndex;
        plan.ModOrder = mods.Select(m => m.ModId).ToList();

        foreach (var (name, path) in modPaths.Roots)
            plan.Roots[name] = path;

        AddUnmanagedNotes(plan, current);
        AddEngineOrderNotes(plan);

        _logger?.LogInformation(
            "Planned deployment: {Files} files from {Mods} mods, {Overlaps} overlaps",
            plan.Files.Count, mods.Count, plan.Overlaps.Count);

        return plan;
    }

    /// <summary>
    /// Flags destinations that already hold a file Modular did not deploy.
    /// </summary>
    /// <param name="plan">The plan being built.</param>
    /// <param name="current">The current manifest, or <c>null</c> when nothing is deployed.</param>
    /// <remarks>
    /// This is not a mod conflict, and reporting it as one was the old behaviour's mistake:
    /// after a deploy every file Modular placed exists in the game folder, so a redeploy
    /// conflicted with itself. A path Modular already owns is silently replaced; only a
    /// genuinely unmanaged file is worth telling the user about, because its vanilla copy
    /// is about to be backed up.
    /// </remarks>
    private static void AddUnmanagedNotes(DeploymentPlan plan, DeploymentManifest? current)
    {
        foreach (var file in plan.Files)
        {
            if (!File.Exists(file.DestinationPath))
                continue;

            if (current?.Find(file.RootName, file.RelativePath) != null)
                continue;

            plan.UnmanagedReplacements.Add(new UnmanagedReplacement(
                file.RootName, file.RelativePath, file.ModId));
        }
    }

    /// <summary>
    /// Notes files that will not overwrite each other but will still fight inside the engine.
    /// </summary>
    /// <param name="plan">The plan being built.</param>
    /// <remarks>
    /// Two archives or paks with the same basename in the same folder are different files,
    /// so last-writer-wins never sees them, but the engine loads both and only one takes
    /// effect. This is the cheap version of that check: same folder, same basename, after
    /// mapping.
    /// </remarks>
    private static void AddEngineOrderNotes(DeploymentPlan plan)
    {
        var loadOrderExtensions = new[] { ".archive", ".pak", ".bin" };

        var groups = plan.Files
            .Where(f => loadOrderExtensions.Contains(
                Path.GetExtension(f.RelativePath), StringComparer.OrdinalIgnoreCase))
            .GroupBy(f => (
                Folder: Path.GetDirectoryName(f.RelativePath)?.Replace('\\', '/') ?? string.Empty,
                Name: Path.GetFileNameWithoutExtension(f.RelativePath)))
            .Where(g => g.Select(f => f.ModId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);

        foreach (var group in groups)
        {
            plan.EngineOrderNotes.Add(new EngineOrderNote(
                group.Key.Folder,
                group.Key.Name,
                group.Select(f => f.ModId).Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        }
    }

    private IReadOnlyList<string> SafeGeneratedPaths(IGameModule module)
    {
        try
        {
            return module.GetGeneratedPaths();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex, "Game module {ModuleId} threw listing generated paths", module.ModuleId);
            return Array.Empty<string>();
        }
    }

    private DeployTarget SafeMapDeployPath(IGameModule module, DeployPathContext context)
    {
        try
        {
            return module.MapDeployPath(context);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex, "Game module {ModuleId} threw mapping {Path}",
                module.ModuleId, context.RelativePath);
            return new DeployTarget(context.RootName, context.RelativePath);
        }
    }

    /// <summary>
    /// Matches a root-relative path against a set of glob patterns.
    /// </summary>
    /// <param name="relativePath">Forward-slashed root-relative path.</param>
    /// <param name="globs">Patterns supporting <c>*</c> and <c>**</c>.</param>
    /// <returns>True when any pattern matches.</returns>
    public static bool MatchesAnyGlob(string relativePath, IReadOnlyList<string> globs)
    {
        foreach (var glob in globs)
        {
            if (MatchesGlob(relativePath, glob))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Matches a path against one glob.
    /// </summary>
    /// <param name="relativePath">Forward-slashed root-relative path.</param>
    /// <param name="glob">
    /// Pattern where <c>*</c> matches within a segment, <c>**</c> matches across segments,
    /// and <c>?</c> matches one non-separator character.
    /// </param>
    /// <returns>True when the pattern matches.</returns>
    public static bool MatchesGlob(string relativePath, string glob)
    {
        if (string.IsNullOrEmpty(glob))
            return false;

        var pattern = GlobToRegex(NormalizeGlob(glob));

        return System.Text.RegularExpressions.Regex.IsMatch(
            NormalizeGlob(relativePath),
            pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static string GlobToRegex(string glob)
    {
        var builder = new System.Text.StringBuilder("^");

        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];

            switch (c)
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    // "**" crosses separators. A trailing "/**" also matches the
                    // directory itself, which is what "r6/cache/**" is meant to say.
                    builder.Append(".*");
                    i++;
                    break;

                case '*':
                    builder.Append("[^/]*");
                    break;

                case '?':
                    builder.Append("[^/]");
                    break;

                default:
                    builder.Append(System.Text.RegularExpressions.Regex.Escape(c.ToString()));
                    break;
            }
        }

        return builder.Append('$').ToString();
    }

    private static string NormalizeGlob(string glob) =>
        glob.Replace('\\', '/').TrimStart('/');
}

/// <summary>
/// A staged mod as the planner sees it: an id, an order, and normalised files.
/// </summary>
/// <param name="ModId">Mod identifier.</param>
/// <param name="StagingPath">Root of the mod's staging tree.</param>
/// <param name="OrderIndex">Position in the load order; later wins.</param>
/// <param name="Files">Normalised staged files.</param>
public readonly record struct DeploymentModInput(
    string ModId,
    string StagingPath,
    int OrderIndex,
    IReadOnlyList<StagedFile> Files);

/// <summary>Root and root-relative path, compared case-insensitively.</summary>
/// <param name="Root">Root name.</param>
/// <param name="Path">Path relative to that root.</param>
public readonly record struct DeployKey(string Root, string Path)
{
    /// <inheritdoc />
    public bool Equals(DeployKey other) =>
        string.Equals(Root, other.Root, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(
        Root.ToLowerInvariant(), Path.ToLowerInvariant());
}

/// <summary>One file the deployer will place.</summary>
public sealed class PlannedFile
{
    /// <summary>Id of the mod that won this destination.</summary>
    public string ModId { get; init; } = string.Empty;

    /// <summary>Name of the destination root.</summary>
    public string RootName { get; init; } = string.Empty;

    /// <summary>Destination path relative to that root, forward-slashed.</summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>Absolute path of the staged source.</summary>
    public string StagedPath { get; init; } = string.Empty;

    /// <summary>Absolute destination path.</summary>
    public string DestinationPath { get; init; } = string.Empty;

    /// <summary>Package type label, when the installer supplied one.</summary>
    public string? PackageType { get; init; }

    /// <summary>Order index of the owning mod.</summary>
    public int OrderIndex { get; init; }

    /// <summary>Whether the file must keep its name and be placed after ordered files.</summary>
    public bool PinnedLast { get; init; }
}

/// <summary>The resolved deployment plan.</summary>
public sealed class DeploymentPlan
{
    /// <summary>Files to place, winners only.</summary>
    public List<PlannedFile> Files { get; } = new();

    /// <summary>Destinations two or more mods contributed, with the winner named last.</summary>
    public List<DeploymentOverlap> Overlaps { get; } = new();

    /// <summary>Files that were not planned, and why.</summary>
    public List<SkippedFile> Skipped { get; } = new();

    /// <summary>Destinations holding a file Modular did not deploy.</summary>
    public List<UnmanagedReplacement> UnmanagedReplacements { get; } = new();

    /// <summary>Files that will both load but fight inside the engine.</summary>
    public List<EngineOrderNote> EngineOrderNotes { get; } = new();

    /// <summary>Resolved named roots at plan time.</summary>
    public Dictionary<string, string> Roots { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mod ids in load order.</summary>
    public List<string> ModOrder { get; set; } = new();

    /// <summary>Overlaps expressed as the shared conflict index.</summary>
    public FileConflictIndex ConflictIndex { get; set; } = new();
}

/// <summary>Two mods shipping the same destination.</summary>
/// <param name="Root">Root name.</param>
/// <param name="RelativePath">Destination relative to that root.</param>
/// <param name="LoserModId">The mod whose file is not placed.</param>
/// <param name="WinnerModId">The mod whose file is placed.</param>
public readonly record struct DeploymentOverlap(
    string Root, string RelativePath, string LoserModId, string WinnerModId);

/// <summary>A staged file that will not be deployed.</summary>
/// <param name="ModId">Owning mod.</param>
/// <param name="Root">Root name.</param>
/// <param name="RelativePath">Path relative to that root.</param>
/// <param name="Reason">Why it was skipped.</param>
public readonly record struct SkippedFile(
    string ModId, string Root, string RelativePath, string Reason);

/// <summary>A destination holding a file Modular did not deploy.</summary>
/// <param name="Root">Root name.</param>
/// <param name="RelativePath">Path relative to that root.</param>
/// <param name="ModId">The mod about to replace it.</param>
public readonly record struct UnmanagedReplacement(
    string Root, string RelativePath, string ModId);

/// <summary>Files that do not overwrite each other but still compete inside the engine.</summary>
/// <param name="Folder">Folder they share.</param>
/// <param name="BaseName">Basename they share.</param>
/// <param name="ModIds">The mods involved.</param>
public readonly record struct EngineOrderNote(
    string Folder, string BaseName, IReadOnlyList<string> ModIds);
