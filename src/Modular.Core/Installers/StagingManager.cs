using Microsoft.Extensions.Logging;
using Modular.Core.Utilities;
using Modular.Sdk.Installers;
using Modular.Sdk.Modules;

namespace Modular.Core.Installers;

/// <summary>
/// Owns the per-mod staging areas that sit between an installer and the game folder.
/// </summary>
/// <remarks>
/// <para>
/// A staged mod lives at <c>&lt;base&gt;/&lt;gameKey&gt;/&lt;modId&gt;/</c> and is laid out
/// as <c>&lt;rootName&gt;/&lt;path relative to that root&gt;</c> — <c>game/…</c>,
/// <c>userconfig/…</c>, and so on. Keeping the root name in the layout is what lets a
/// mod that installs outside the game directory survive a restaging.
/// </para>
/// <para>
/// Staging is persistent, not temporary: the staged tree is the deployable source of
/// truth for a mod, so a reorder or a redeploy never needs the original archive again.
/// </para>
/// </remarks>
public class StagingManager
{
    private readonly ILogger<StagingManager>? _logger;

    /// <summary>
    /// Creates a new staging manager.
    /// </summary>
    /// <param name="baseStagingPath">Root directory for staging areas (e.g., ~/.config/Modular/staging/).</param>
    /// <param name="logger">Optional logger.</param>
    public StagingManager(string baseStagingPath, ILogger<StagingManager>? logger = null)
    {
        BasePath = baseStagingPath;
        _logger = logger;
        Directory.CreateDirectory(BasePath);
    }

    /// <summary>Root directory holding every game's staging areas.</summary>
    public string BasePath { get; }

    /// <summary>
    /// Directory holding everything staged for one game.
    /// </summary>
    /// <param name="gameKey">Game key, normally the Steam AppID.</param>
    /// <returns>The absolute path. Not created.</returns>
    public string GetGameStagingDirectory(string gameKey) =>
        Path.Combine(BasePath, SanitizeSegment(gameKey));

    /// <summary>
    /// Directory holding one staged mod.
    /// </summary>
    /// <param name="gameKey">Game key, normally the Steam AppID.</param>
    /// <param name="modId">Mod identifier.</param>
    /// <returns>The absolute path. Not created.</returns>
    public string GetModStagingDirectory(string gameKey, string modId) =>
        Path.Combine(GetGameStagingDirectory(gameKey), SanitizeSegment(modId));

    /// <summary>
    /// Directory where files displaced by a user edit are preserved rather than deleted.
    /// </summary>
    /// <param name="gameKey">Game key.</param>
    /// <returns>The absolute path. Not created.</returns>
    public string GetOverwriteDirectory(string gameKey) =>
        Path.Combine(GetGameStagingDirectory(gameKey), "_overwrite");

    /// <summary>
    /// Opens a staging session for one mod of one game.
    /// </summary>
    /// <param name="changesetId">Changeset this staging belongs to.</param>
    /// <param name="gameKey">Game key, normally the Steam AppID.</param>
    /// <param name="modId">Mod identifier.</param>
    /// <param name="replaceExisting">
    /// Whether to clear any previously staged content for this mod first. A restage
    /// should; resuming should not.
    /// </param>
    /// <returns>The session. Persistent: disposing it does not delete the staged tree.</returns>
    public StagingSession CreateSession(
        string changesetId,
        string gameKey,
        string modId,
        bool replaceExisting = true)
    {
        var directory = GetModStagingDirectory(gameKey, modId);

        if (replaceExisting && Directory.Exists(directory))
        {
            _logger?.LogDebug("Clearing previous staging for {ModId} at {Path}", modId, directory);
            Directory.Delete(directory, recursive: true);
        }

        Directory.CreateDirectory(directory);

        _logger?.LogInformation(
            "Staging {ModId} for game {GameKey} at {Path}", modId, gameKey, directory);

        return new StagingSession(changesetId, directory, _logger, persistent: true);
    }

    /// <summary>
    /// Opens a throwaway staging session, deleted when the session is disposed
    /// uncommitted.
    /// </summary>
    /// <param name="changesetId">Changeset this staging belongs to.</param>
    /// <returns>The session.</returns>
    /// <remarks>
    /// For callers that only want stage-then-commit atomicity and keep no managed
    /// deployment, such as a legacy direct install.
    /// </remarks>
    public StagingSession CreateTransientSession(string changesetId)
    {
        var directory = Path.Combine(BasePath, "_transient", SanitizeSegment(changesetId));

        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);

        Directory.CreateDirectory(directory);

        return new StagingSession(changesetId, directory, _logger, persistent: false);
    }

    /// <summary>
    /// Deletes a mod's staged tree.
    /// </summary>
    /// <param name="gameKey">Game key.</param>
    /// <param name="modId">Mod identifier.</param>
    /// <returns>True when a directory was removed.</returns>
    /// <remarks>
    /// Only safe once the mod is no longer part of a deployment: with hardlink or
    /// symlink deployment the game folder still references these bytes.
    /// </remarks>
    public bool DeleteModStaging(string gameKey, string modId)
    {
        var directory = GetModStagingDirectory(gameKey, modId);
        if (!Directory.Exists(directory))
            return false;

        Directory.Delete(directory, recursive: true);
        _logger?.LogInformation("Deleted staging for {ModId} ({GameKey})", modId, gameKey);
        return true;
    }

    /// <summary>
    /// Rewrites an install plan so its installer writes into a staging tree instead of
    /// the game directory.
    /// </summary>
    /// <param name="plan">The plan to redirect. Mutated in place.</param>
    /// <param name="gameDirectory">The real game directory the plan was built against.</param>
    /// <param name="stagingRoot">Root of this mod's staging tree.</param>
    /// <returns>The redirection, which knows how to normalise the resulting paths.</returns>
    /// <remarks>
    /// <para>
    /// Redirection is generic rather than per-installer. An installer's destinations are
    /// relative to <see cref="InstallPlan.TargetDirectory"/>, and that directory is not
    /// always the game root: <c>UnrealPakInstaller</c> points it at
    /// <c>…/Content/Paks/~mods</c>. The offset between the game root and the plan's
    /// target is therefore prefixed onto every destination, in one place, before
    /// anything else looks at a path.
    /// </para>
    /// <para>
    /// The redirected target becomes the staging root itself, and each destination
    /// gains its root name as a leading segment. An operation carrying an explicit
    /// <see cref="FileOperation.TargetRoot"/> is already root-relative, so it is not
    /// offset — it just lands under its own root.
    /// </para>
    /// </remarks>
    public StagingRedirection RedirectPlan(
        InstallPlan plan,
        string gameDirectory,
        string stagingRoot)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var redirection = new StagingRedirection(
            gameDirectory, stagingRoot, GetPlanOffset(gameDirectory, plan.TargetDirectory));

        foreach (var operation in plan.Operations)
        {
            var normalised = redirection.Normalize(operation);

            // Recorded before the destination is rewritten: afterwards the operation's
            // destination already carries its root segment, so normalising it a second time
            // would prefix the root twice.
            redirection.Record(normalised);

            // The destination is now "<root>/<path within that root>", relative to the
            // staging root, which is the same shape for every root.
            operation.DestinationPath = normalised.RootName +
                (normalised.RelativePath.Length == 0 ? string.Empty : "/" + normalised.RelativePath);
        }

        plan.TargetDirectory = stagingRoot;

        // Every destination now sits under the staging root, whatever root it names.
        plan.RootDirectories = null;

        _logger?.LogDebug(
            "Redirected plan into {StagingRoot} (offset '{Offset}')",
            stagingRoot, redirection.PlanOffset);

        return redirection;
    }

    /// <summary>
    /// Reads a staged tree back as a set of <c>(root, root-relative path)</c> pairs.
    /// </summary>
    /// <param name="stagingRoot">Root of a mod's staging tree.</param>
    /// <returns>The staged files, ordered by root then path.</returns>
    /// <remarks>
    /// The tree on disk is the source of truth, not an installer's own bookkeeping:
    /// installers disagree about whether they record absolute or relative paths, and
    /// walking the result sidesteps that entirely.
    /// </remarks>
    public static IReadOnlyList<StagedFile> EnumerateStagedFiles(string stagingRoot)
    {
        if (!Directory.Exists(stagingRoot))
            return Array.Empty<StagedFile>();

        var files = new List<StagedFile>();

        // A file sitting directly in the staging root has no root segment. That means an
        // installer wrote somewhere the plan did not ask it to; attribute it to the game
        // root rather than dropping it silently.
        foreach (var stray in Directory.EnumerateFiles(stagingRoot))
            files.Add(new StagedFile(ModRoots.Game, Path.GetFileName(stray)));

        foreach (var rootDir in Directory.EnumerateDirectories(stagingRoot))
        {
            var rootName = Path.GetFileName(rootDir);

            // Displaced user edits live alongside the roots but are not part of the mod.
            if (rootName.StartsWith('_'))
                continue;

            foreach (var file in Directory.EnumerateFiles(rootDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(rootDir, file).Replace('\\', '/');
                files.Add(new StagedFile(rootName, relative));
            }
        }

        return files
            .OrderBy(f => f.RootName, StringComparer.Ordinal)
            .ThenBy(f => f.RelativePath, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Builds the staged-mod view a module's validation hook expects.
    /// </summary>
    /// <param name="modId">Mod identifier.</param>
    /// <param name="stagingRoot">Root of the mod's staging tree.</param>
    /// <param name="labels">
    /// Optional package-type and route-source labels, keyed by
    /// <c>&lt;root&gt;/&lt;relative path&gt;</c>, as recorded by the installer.
    /// </param>
    /// <returns>The staged mod.</returns>
    public static StagedMod BuildStagedMod(
        string modId,
        string stagingRoot,
        IReadOnlyDictionary<string, StagedFile>? labels = null)
    {
        var files = EnumerateStagedFiles(stagingRoot);

        if (labels is { Count: > 0 })
        {
            files = files
                .Select(f => labels.TryGetValue($"{f.RootName}/{f.RelativePath}", out var labelled)
                    ? new StagedFile(f.RootName, f.RelativePath, labelled.PackageType, labelled.RouteSource)
                    : f)
                .ToList();
        }

        return new StagedMod
        {
            ModId = modId,
            StagingPath = stagingRoot,
            Files = files
        };
    }

    /// <summary>
    /// Computes the path offset between a game root and a plan's target directory.
    /// </summary>
    /// <param name="gameDirectory">The game root.</param>
    /// <param name="planTarget">The plan's target directory.</param>
    /// <returns>
    /// A forward-slashed relative path, or an empty string when the plan targets the
    /// game root (or targets something outside it, which cannot be offset).
    /// </returns>
    internal static string GetPlanOffset(string gameDirectory, string? planTarget)
    {
        if (string.IsNullOrEmpty(planTarget) || string.IsNullOrEmpty(gameDirectory))
            return string.Empty;

        string relative;
        try
        {
            relative = Path.GetRelativePath(
                Path.GetFullPath(gameDirectory), Path.GetFullPath(planTarget));
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }

        if (relative is "." or "")
            return string.Empty;

        // A target outside the game directory has no game-relative offset. Treating it
        // as the root is the safe reading: nothing is silently written above staging.
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return string.Empty;

        return relative.Replace('\\', '/').TrimEnd('/');
    }

    private static string SanitizeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value
            .Select(c => invalid.Contains(c) || c == Path.DirectorySeparatorChar ? '_' : c)
            .ToArray());

        return cleaned.Trim('.', ' ') is { Length: > 0 } trimmed ? trimmed : "unknown";
    }
}

/// <summary>
/// The mapping from an install plan's own paths to normalised
/// <c>(root, root-relative path)</c> pairs inside a staging tree.
/// </summary>
/// <remarks>
/// Every later stage — module deploy mapping, staged validation, generated-path
/// matching and the deployment planner — sees only normalised pairs, so no stage has
/// to know that an installer happened to target a subdirectory of the game.
/// </remarks>
public sealed class StagingRedirection
{
    internal StagingRedirection(string gameDirectory, string stagingRoot, string planOffset)
    {
        GameDirectory = gameDirectory;
        StagingRoot = stagingRoot;
        PlanOffset = planOffset;
    }

    /// <summary>The real game directory the plan was built against.</summary>
    public string GameDirectory { get; }

    /// <summary>Root of the mod's staging tree; the redirected plan's target directory.</summary>
    public string StagingRoot { get; }

    /// <summary>
    /// Game-relative path of the plan's original target directory, or an empty string
    /// when the plan targeted the game root.
    /// </summary>
    public string PlanOffset { get; }

    /// <summary>
    /// Normalises one operation's destination into a root and a root-relative path.
    /// </summary>
    /// <param name="operation">The operation, with its original destination.</param>
    /// <returns>The normalised pair, carrying the operation's routing labels.</returns>
    public StagedFile Normalize(FileOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var root = string.IsNullOrEmpty(operation.TargetRoot)
            ? ModRoots.Game
            : operation.TargetRoot;

        var destination = (operation.DestinationPath ?? string.Empty)
            .Replace('\\', '/')
            .TrimStart('/');

        // Only game-root destinations carry the plan's offset. An operation that named
        // its own root expressed a path relative to that root already.
        var isGameRoot = string.Equals(root, ModRoots.Game, StringComparison.OrdinalIgnoreCase);

        var relative = isGameRoot && PlanOffset.Length > 0
            ? (destination.Length == 0 ? PlanOffset : $"{PlanOffset}/{destination}")
            : destination;

        return new StagedFile(
            root, relative, operation.PackageType, operation.RouteSource);
    }

    private readonly Dictionary<string, StagedFile> _normalized =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The normalised pairs this redirection produced, keyed by
    /// <c>&lt;root&gt;/&lt;relative path&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Carries each file's package type and route source, which the staged tree on disk
    /// cannot: a path says where a file went, not what kind of package it is or which rule
    /// put it there.
    /// </remarks>
    public IReadOnlyDictionary<string, StagedFile> NormalizedFiles => _normalized;

    /// <summary>
    /// Records a normalised pair, keeping the strongest routing label when a destination is
    /// produced more than once.
    /// </summary>
    /// <param name="file">The normalised staged file.</param>
    internal void Record(StagedFile file)
    {
        var key = $"{file.RootName}/{file.RelativePath}";

        // Two entries can land on one destination (a flattened archive, or a rename). The
        // stronger signal wins, because that is the rule that actually decided the path.
        if (_normalized.TryGetValue(key, out var existing) &&
            existing.RouteSource <= file.RouteSource)
        {
            return;
        }

        _normalized[key] = file;
    }

    /// <summary>
    /// Resolves the absolute staged path of a normalised pair.
    /// </summary>
    /// <param name="file">The normalised staged file.</param>
    /// <returns>The absolute path inside the staging tree.</returns>
    public string ResolveStagedPath(StagedFile file) =>
        PathSanitizer.SanitizeEntryPath(
            $"{file.RootName}/{file.RelativePath}".TrimEnd('/'), StagingRoot);
}

/// <summary>
/// Represents an active staging session. Files are extracted here, verified,
/// then committed to the final target directory.
/// </summary>
public class StagingSession : IDisposable
{
    private readonly ILogger? _logger;
    private readonly bool _persistent;
    private bool _committed;
    private bool _disposed;

    /// <summary>
    /// Unique identifier for this changeset.
    /// </summary>
    public string ChangesetId { get; }

    /// <summary>
    /// Path to the staging directory.
    /// </summary>
    public string StagingDirectory { get; }

    /// <summary>
    /// Files staged so far (relative path → full staged path).
    /// </summary>
    public Dictionary<string, string> StagedFiles { get; } = new();

    /// <summary>
    /// Whether the staged tree outlives the session.
    /// </summary>
    /// <remarks>
    /// True for a managed mod, whose staged tree is the deployable source of truth.
    /// False for a transient stage-then-commit install, which cleans up after itself.
    /// </remarks>
    public bool IsPersistent => _persistent;

    internal StagingSession(
        string changesetId,
        string stagingDirectory,
        ILogger? logger,
        bool persistent = false)
    {
        ChangesetId = changesetId;
        StagingDirectory = stagingDirectory;
        _logger = logger;
        _persistent = persistent;
    }

    /// <summary>
    /// Resolves and creates the staging path for a relative file entry.
    /// </summary>
    /// <param name="relativePath">Relative path within the staging area.</param>
    /// <returns>Full path within the staging directory.</returns>
    public string GetStagedPath(string relativePath)
    {
        var fullPath = PathSanitizer.SanitizeEntryPath(relativePath, StagingDirectory);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        return fullPath;
    }

    /// <summary>
    /// Records that a file has been staged.
    /// </summary>
    /// <param name="relativePath">Path relative to the staging directory.</param>
    /// <param name="stagedFullPath">Absolute path of the staged file.</param>
    public void RecordStagedFile(string relativePath, string stagedFullPath)
    {
        StagedFiles[relativePath] = stagedFullPath;
    }

    /// <summary>
    /// Commits all staged files to the target directory.
    /// Creates backups of existing files.
    /// </summary>
    /// <param name="targetDirectory">The final installation target directory.</param>
    /// <param name="createBackups">Whether to back up existing files before overwriting.</param>
    /// <param name="keepStagedCopy">
    /// Whether the staged files survive the commit. Required for a managed mod, whose
    /// staged tree must remain deployable; a transient session moves instead of copying.
    /// </param>
    /// <returns>Commit result with lists of installed and backed-up files.</returns>
    public StagingCommitResult Commit(
        string targetDirectory,
        bool createBackups = true,
        bool keepStagedCopy = false)
    {
        var result = new StagingCommitResult();

        try
        {
            foreach (var (relativePath, stagedPath) in StagedFiles)
            {
                var destPath = PathSanitizer.SanitizeEntryPath(relativePath, targetDirectory);
                var destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                // Backup existing file
                if (File.Exists(destPath) && createBackups)
                {
                    var backupPath = destPath + ".backup";
                    File.Copy(destPath, backupPath, true);
                    result.BackedUpFiles.Add(backupPath);
                }

                if (keepStagedCopy)
                {
                    File.Copy(stagedPath, destPath, overwrite: true);
                }
                else
                {
                    File.Move(stagedPath, destPath, overwrite: true);
                }

                result.InstalledFiles.Add(destPath);
            }

            _committed = true;
            result.Success = true;
            _logger?.LogInformation(
                "Committed staging session {ChangesetId}: {Count} files",
                ChangesetId, result.InstalledFiles.Count);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _logger?.LogError(ex, "Failed to commit staging session {ChangesetId}", ChangesetId);
        }

        return result;
    }

    /// <summary>
    /// Rolls back the staging session by deleting the staging directory.
    /// </summary>
    /// <remarks>
    /// A persistent session keeps its tree: for a managed mod the staged files are the
    /// deployable copy, and a failed deploy must leave a recoverable state rather than
    /// discarding them.
    /// </remarks>
    public void Rollback()
    {
        if (_committed || _persistent)
            return;

        try
        {
            if (Directory.Exists(StagingDirectory))
            {
                Directory.Delete(StagingDirectory, recursive: true);
                _logger?.LogInformation("Rolled back staging session {ChangesetId}", ChangesetId);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to clean up staging directory during rollback");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (!_committed)
            Rollback();
    }
}

/// <summary>
/// Result of committing a staging session.
/// </summary>
public class StagingCommitResult
{
    /// <summary>Whether every staged file reached the target.</summary>
    public bool Success { get; set; }

    /// <summary>Absolute paths of the files placed in the target directory.</summary>
    public List<string> InstalledFiles { get; set; } = new();

    /// <summary>Absolute paths of the backups taken before overwriting.</summary>
    public List<string> BackedUpFiles { get; set; } = new();

    /// <summary>Failure reason, when <see cref="Success"/> is false.</summary>
    public string? Error { get; set; }
}
