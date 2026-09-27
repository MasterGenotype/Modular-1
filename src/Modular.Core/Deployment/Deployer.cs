using Microsoft.Extensions.Logging;
using Modular.Sdk.Modules;

namespace Modular.Core.Deployment;

/// <summary>
/// Applies a <see cref="DeploymentPlan"/> to a game: purge what is no longer wanted,
/// back up vanilla files once, place the planned files, then record what was done.
/// </summary>
/// <remarks>
/// <para>
/// The deployer is the only thing that writes to a game root, and the only owner of
/// vanilla backups. That concentration is deliberate: the installers' own backup code
/// cannot fire any more, because they now write into an empty staging tree.
/// </para>
/// <para>
/// Nothing is destroyed that Modular cannot prove it placed. A deployed file whose
/// recorded identity no longer matches was edited in place, or had its link replaced by
/// an editor's atomic save; it is moved into <c>_overwrite/</c> and reported rather than
/// deleted.
/// </para>
/// </remarks>
public sealed class Deployer : IDeployer
{
    private readonly StagingLayout _layout;
    private readonly ILogger<Deployer>? _logger;
    private readonly Dictionary<DeploymentMethod, IDeploymentStrategy> _strategies;

    /// <summary>
    /// Creates a deployer.
    /// </summary>
    /// <param name="layout">Where staging, backups and manifests live.</param>
    /// <param name="strategies">
    /// Placement strategies. Defaults to the three built-in ones; a test may substitute
    /// its own to force a failure.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public Deployer(
        StagingLayout layout,
        IEnumerable<IDeploymentStrategy>? strategies = null,
        ILogger<Deployer>? logger = null)
    {
        _layout = layout;
        _logger = logger;

        var provided = strategies?.ToList() ?? new List<IDeploymentStrategy>
        {
            new HardlinkDeploymentStrategy(),
            new SymlinkDeploymentStrategy(),
            new CopyDeploymentStrategy()
        };

        _strategies = provided.ToDictionary(s => s.Method);
    }

    /// <summary>
    /// Deploys a plan.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <param name="module">The module owning the game.</param>
    /// <param name="plan">The plan to apply.</param>
    /// <param name="settings">Deployment method and fallback.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What was placed, preserved, backed up and reported.</returns>
    public async Task<DeploymentResult> DeployAsync(
        GameInstallation installation,
        IGameModule module,
        DeploymentPlan plan,
        DeploymentSettings settings,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(plan);

        var gameStaging = _layout.GetGameStagingDirectory(installation.GameKey);
        var result = new DeploymentResult
        {
            GameKey = installation.GameKey,
            RequestedMethod = settings.Method,
            MethodSource = settings.Source
        };

        var previous = DeploymentManifest.Load(gameStaging, installation.GameKey);
        var constraints = SafeDeployConstraints(module);

        // A stale build id means the game was updated under us. The old vanilla backups
        // belong to the previous build and must not be restored over the new files.
        var buildChanged =
            previous.Files.Count > 0 &&
            !string.Equals(previous.BuildId, installation.BuildId, StringComparison.Ordinal);

        if (buildChanged)
            result.Warnings.Add(BuildChangedWarning(previous.BuildId, installation.BuildId));

        // Pre-flight: probe once per root, before a single game file is touched.
        var probeFailures = Probe(plan, settings.Method);

        foreach (var (root, reason) in probeFailures)
        {
            if (settings.Fallback == DeploymentFallback.Fail)
            {
                result.Error =
                    $"Deployment method '{settings.Method.ToName()}' is unavailable for root " +
                    $"'{root}': {reason}. Configured fallback is 'fail', so nothing was changed.";
                return result;
            }

            result.Warnings.Add(
                $"Root '{root}' cannot use {settings.Method.ToName()} ({reason}); " +
                "those files will be copied.");
        }

        var fallbackRoots = probeFailures.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var generatedGlobs = SafeGeneratedPaths(module);

        var manifest = new DeploymentManifest
        {
            GameKey = installation.GameKey,
            BuildId = installation.BuildId,
            RequestedMethod = settings.Method.ToName(),
            ModOrder = plan.ModOrder.ToList(),
            Roots = new Dictionary<string, string>(plan.Roots)
        };

        try
        {
            Purge(plan, previous, manifest, result, generatedGlobs, buildChanged, installation);

            foreach (var file in plan.Files)
            {
                ct.ThrowIfCancellationRequested();
                PlaceFile(file, plan, previous, manifest, result, settings, constraints, fallbackRoots, installation);
            }

            manifest.Save(gameStaging);
            result.Manifest = manifest;

            await WriteLoadOrderAsync(installation, module, plan, result, ct);

            CollectLaunchRequirements(installation, module, plan, result);

            result.Success = result.Error == null;
        }
        catch (OperationCanceledException)
        {
            // The manifest still records what actually reached the game folder, so a
            // cancelled deployment stays purgeable.
            manifest.Save(gameStaging);
            throw;
        }
        catch (Exception ex)
        {
            manifest.Save(gameStaging);
            result.Error = ex.Message;
            _logger?.LogError(ex, "Deployment failed for {GameKey}", installation.GameKey);
        }

        return result;
    }

    /// <summary>
    /// Removes everything Modular deployed for a game and restores the vanilla files.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <returns>What was removed, preserved and restored.</returns>
    public DeploymentResult Purge(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);

        var gameStaging = _layout.GetGameStagingDirectory(installation.GameKey);
        var previous = DeploymentManifest.Load(gameStaging, installation.GameKey);

        var result = new DeploymentResult { GameKey = installation.GameKey };
        var manifest = new DeploymentManifest
        {
            GameKey = installation.GameKey,
            BuildId = installation.BuildId
        };

        var buildChanged =
            previous.Files.Count > 0 &&
            !string.Equals(previous.BuildId, installation.BuildId, StringComparison.Ordinal);

        if (buildChanged)
            result.Warnings.Add(BuildChangedWarning(previous.BuildId, installation.BuildId));

        Purge(
            new DeploymentPlan(), previous, manifest, result,
            Array.Empty<string>(), buildChanged, installation);

        manifest.Save(gameStaging);
        result.Manifest = manifest;
        result.Success = true;
        return result;
    }

    /// <summary>
    /// Reports which methods work for each of a plan's roots.
    /// </summary>
    /// <param name="plan">A plan naming the roots to probe.</param>
    /// <param name="method">The method to probe.</param>
    /// <returns>Root name to failure reason, for the roots where the method fails.</returns>
    public Dictionary<string, string> Probe(DeploymentPlan plan, DeploymentMethod method)
    {
        var failures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!_strategies.TryGetValue(method, out var strategy))
            return failures;

        var roots = plan.Files
            .Select(f => f.RootName)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (!plan.Roots.TryGetValue(root, out var rootPath))
                continue;

            var reason = strategy.Probe(_layout.BasePath, rootPath);
            if (reason != null)
                failures[root] = reason;
        }

        return failures;
    }

    /// <summary>
    /// Probes every method against every root of a plan.
    /// </summary>
    /// <param name="plan">A plan naming the roots to probe.</param>
    /// <returns>Per root, the methods that work.</returns>
    public Dictionary<string, List<DeploymentMethod>> ProbeAll(DeploymentPlan plan)
    {
        var usable = new Dictionary<string, List<DeploymentMethod>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (root, rootPath) in plan.Roots)
        {
            var methods = new List<DeploymentMethod>();

            foreach (var (method, strategy) in _strategies)
            {
                if (strategy.Probe(_layout.BasePath, rootPath) == null)
                    methods.Add(method);
            }

            usable[root] = methods;
        }

        return usable;
    }

    /// <summary>
    /// Removes files from the previous deployment that the new plan does not keep.
    /// </summary>
    /// <remarks>
    /// The identity check is what makes this safe. A file that still matches was placed by
    /// Modular and nothing has touched it, so it can go; one that drifted holds the user's
    /// bytes and is moved aside instead. Files under a generated path are never Modular's,
    /// so they are left exactly where they are.
    /// </remarks>
    private void Purge(
        DeploymentPlan plan,
        DeploymentManifest previous,
        DeploymentManifest manifest,
        DeploymentResult result,
        IReadOnlyList<string> generatedGlobs,
        bool buildChanged,
        GameInstallation installation)
    {
        var keep = plan.Files
            .Select(f => new DeployKey(f.RootName, f.RelativePath))
            .ToHashSet();

        foreach (var record in previous.Files)
        {
            var key = new DeployKey(record.Root, record.Path);

            // Still wanted: PlaceFile decides whether it needs rewriting, and carries the
            // vanilla-backup reference forward.
            if (keep.Contains(key))
                continue;

            if (DeploymentPlanner.MatchesAnyGlob(record.Path, generatedGlobs))
            {
                result.GeneratedPathsLeftAlone.Add($"{record.Root}/{record.Path}");
                continue;
            }

            if (!previous.Roots.TryGetValue(record.Root, out var rootPath))
            {
                result.Warnings.Add(
                    $"Cannot purge {record.Root}/{record.Path}: root '{record.Root}' is " +
                    "no longer resolvable.");
                continue;
            }

            var destination = Path.Combine(
                rootPath, record.Path.Replace('/', Path.DirectorySeparatorChar));

            var method = DeploymentMethods.ParseMethod(record.Method) ?? DeploymentMethod.Copy;
            var strategy = _strategies[method];
            var current = strategy.ReadIdentity(destination);

            if (current == null)
            {
                // Already gone — a Steam file verification, or the user deleted it.
                RestoreVanilla(record, destination, result, buildChanged, installation);
                continue;
            }

            if (!strategy.Matches(record.ToIdentity(), current.Value))
            {
                if (buildChanged)
                {
                    // After a game update the file at this path is almost certainly the
                    // store's new version, not a user edit. Moving it aside would delete a
                    // game file, and restoring our old backup over it would corrupt the
                    // install, so it is left exactly where it is and reported.
                    result.StaleBackupsSkipped.Add($"{record.Root}/{record.Path}");
                    continue;
                }

                PreserveDisplaced(record, destination, result, installation);
                continue;
            }

            strategy.Remove(destination);
            result.Removed.Add($"{record.Root}/{record.Path}");

            RestoreVanilla(record, destination, result, buildChanged, installation);
            TryRemoveEmptyDirectory(Path.GetDirectoryName(destination), rootPath);
        }

        // Manifest files Modular wrote get their pre-existing version back.
        foreach (var written in previous.WrittenManifests)
        {
            if (plan.Files.Count > 0)
            {
                // A redeploy rewrites them; only a full purge restores them.
                manifest.WrittenManifests.Add(written);
                continue;
            }

            var backup = written + VanillaBackupSuffix;
            if (!File.Exists(backup))
                continue;

            DeploymentFile.RemoveEntry(written);
            File.Move(backup, written);
            result.Restored.Add(written);
        }
    }

    /// <summary>
    /// The warning shown when the game was updated since the last deployment.
    /// </summary>
    /// <param name="previousBuildId">Build the deployment was made against.</param>
    /// <param name="currentBuildId">Build now installed.</param>
    /// <returns>The warning text.</returns>
    private static string BuildChangedWarning(string? previousBuildId, string? currentBuildId) =>
        $"The game was updated since the last deployment (build {previousBuildId ?? "unknown"} → " +
        $"{currentBuildId ?? "unknown"}). Backups from the old build will not be restored over " +
        "the updated files; verify your game files if anything looks wrong.";

    /// <summary>Suffix used for a manifest file's pre-existing version.</summary>
    private const string VanillaBackupSuffix = ".modular-vanilla";

    private void PlaceFile(
        PlannedFile file,
        DeploymentPlan plan,
        DeploymentManifest previous,
        DeploymentManifest manifest,
        DeploymentResult result,
        DeploymentSettings settings,
        IReadOnlyList<DeployConstraint> constraints,
        HashSet<string> fallbackRoots,
        GameInstallation installation)
    {
        if (!File.Exists(file.StagedPath))
        {
            result.Warnings.Add(
                $"Staged file missing for {file.ModId}: {file.RootName}/{file.RelativePath}");
            return;
        }

        var record = new DeployedFileRecord
        {
            Root = file.RootName,
            Path = file.RelativePath,
            ModId = file.ModId,
            StagedPath = file.StagedPath,
            PackageType = file.PackageType
        };

        var existing = previous.Find(file.RootName, file.RelativePath);

        // A pre-existing file Modular does not own is vanilla, and its only backup is
        // taken here, once, before it is replaced.
        if (existing == null && File.Exists(file.DestinationPath))
        {
            record.VanillaBackup = BackUpVanilla(file.DestinationPath, file, installation, result);
            result.ReplacedUnmanaged.Add($"{file.RootName}/{file.RelativePath}");
        }
        else
        {
            record.VanillaBackup = existing?.VanillaBackup;
        }

        var requested = ResolveMethod(file, settings, constraints, fallbackRoots, result);
        var strategy = _strategies[requested];

        // Incremental: a file already matching what we recorded needs no rewrite. This is
        // what keeps a reorder of a large mod list from rewriting gigabytes.
        if (existing != null &&
            string.Equals(existing.ModId, file.ModId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.Method, requested.ToName(), StringComparison.Ordinal) &&
            strategy.ReadIdentity(file.DestinationPath) is { } current &&
            strategy.Matches(existing.ToIdentity(), current))
        {
            record.SetIdentity(existing.ToIdentity());
            record.Requested = existing.Requested;
            manifest.Files.Add(record);
            result.Unchanged.Add($"{file.RootName}/{file.RelativePath}");
            return;
        }

        var destinationDir = Path.GetDirectoryName(file.DestinationPath);
        if (!string.IsNullOrEmpty(destinationDir))
            Directory.CreateDirectory(destinationDir);

        // A destination that drifted must not be clobbered, even when it is still wanted.
        if (existing != null && strategy.ReadIdentity(file.DestinationPath) is { } present)
        {
            var existingMethod =
                DeploymentMethods.ParseMethod(existing.Method) ?? requested;

            if (!_strategies[existingMethod].Matches(existing.ToIdentity(), present))
                PreserveDisplaced(existing, file.DestinationPath, result, installation);
        }

        try
        {
            record.SetIdentity(strategy.Place(file.StagedPath, file.DestinationPath));
            if (requested != settings.Method)
                record.Requested = settings.Method.ToName();

            result.Placed.Add($"{file.RootName}/{file.RelativePath}");
        }
        catch (DeploymentMethodUnavailableException ex)
        {
            if (settings.Fallback == DeploymentFallback.Fail)
            {
                result.Error =
                    $"{file.RootName}/{file.RelativePath}: {ex.Message} " +
                    "Configured fallback is 'fail'.";
                throw new DeploymentAbortedException(result.Error, ex);
            }

            var copy = _strategies[DeploymentMethod.Copy];
            record.SetIdentity(copy.Place(file.StagedPath, file.DestinationPath));
            record.Requested = settings.Method.ToName();

            result.Placed.Add($"{file.RootName}/{file.RelativePath}");
            result.FellBackToCopy.Add($"{file.RootName}/{file.RelativePath}");
            _logger?.LogDebug(
                "Fell back to copy for {Path}: {Reason}", file.DestinationPath, ex.Message);
        }

        manifest.Files.Add(record);
    }

    /// <summary>
    /// Chooses the method for one file: a module constraint first, then a failed probe for
    /// its root, then the user's choice.
    /// </summary>
    private DeploymentMethod ResolveMethod(
        PlannedFile file,
        DeploymentSettings settings,
        IReadOnlyList<DeployConstraint> constraints,
        HashSet<string> fallbackRoots,
        DeploymentResult result)
    {
        foreach (var constraint in constraints)
        {
            if (!DeploymentPlanner.MatchesGlob(file.RelativePath, constraint.PathGlob) &&
                constraint.PathGlob != "*")
            {
                continue;
            }

            if (constraint.ForceCopy)
            {
                result.ConstraintApplied.Add(
                    $"{file.RootName}/{file.RelativePath}: copied ({constraint.Reason})");
                return DeploymentMethod.Copy;
            }

            if (constraint.DisallowSymlink && settings.Method == DeploymentMethod.Symlink)
            {
                result.ConstraintApplied.Add(
                    $"{file.RootName}/{file.RelativePath}: not symlinked ({constraint.Reason})");
                return DeploymentMethod.Hardlink;
            }
        }

        if (!fallbackRoots.Contains(file.RootName))
            return settings.Method;

        // The pre-flight probe already proved the method cannot work for this root, so
        // there is nothing to gain from failing per file — but the substitution is still
        // recorded, because a mixed deployment has to be visible.
        result.FellBackToCopy.Add($"{file.RootName}/{file.RelativePath}");
        return DeploymentMethod.Copy;
    }

    /// <summary>
    /// Copies a vanilla file into the build-stamped backup store, once.
    /// </summary>
    /// <returns>The backup path, relative to the backup root.</returns>
    private string? BackUpVanilla(
        string destinationPath,
        PlannedFile file,
        GameInstallation installation,
        DeploymentResult result)
    {
        try
        {
            var relative = Path.Combine(
                file.RootName, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var backupPath = Path.Combine(
                _layout.GetBackupDirectory(installation.GameKey, installation.BuildId), relative);

            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);

            if (!File.Exists(backupPath))
            {
                File.Copy(destinationPath, backupPath);
                result.VanillaBackedUp.Add($"{file.RootName}/{file.RelativePath}");
            }

            return relative.Replace(Path.DirectorySeparatorChar, '/');
        }
        catch (Exception ex)
        {
            result.Warnings.Add(
                $"Could not back up the existing {file.RootName}/{file.RelativePath}: {ex.Message}");
            return null;
        }
    }

    private void RestoreVanilla(
        DeployedFileRecord record,
        string destinationPath,
        DeploymentResult result,
        bool buildChanged,
        GameInstallation installation)
    {
        if (record.VanillaBackup == null)
            return;

        if (buildChanged)
        {
            result.StaleBackupsSkipped.Add($"{record.Root}/{record.Path}");
            return;
        }

        var backupPath = Path.Combine(
            _layout.GetBackupDirectory(installation.GameKey, installation.BuildId),
            record.VanillaBackup.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(backupPath))
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            DeploymentFile.RemoveEntry(destinationPath);
            File.Copy(backupPath, destinationPath);
            result.Restored.Add($"{record.Root}/{record.Path}");
        }
        catch (Exception ex)
        {
            result.Warnings.Add(
                $"Could not restore the original {record.Root}/{record.Path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Moves a file Modular no longer recognises into the overwrite store.
    /// </summary>
    /// <remarks>
    /// A drifted file is the user's work — an in-place edit, or an editor's atomic save
    /// that replaced our link with a real file. Deleting it would destroy that silently.
    /// </remarks>
    private void PreserveDisplaced(
        DeployedFileRecord record,
        string destinationPath,
        DeploymentResult result,
        GameInstallation installation)
    {
        try
        {
            var target = Path.Combine(
                _layout.GetOverwriteDirectory(installation.GameKey),
                record.Root,
                record.Path.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            if (File.Exists(target))
                File.Delete(target);

            File.Move(destinationPath, target);

            result.Displaced.Add($"{record.Root}/{record.Path}");
            result.Warnings.Add(
                $"{record.Root}/{record.Path} had been changed since it was deployed; " +
                $"it was moved to {target} rather than deleted.");
        }
        catch (Exception ex)
        {
            result.Warnings.Add(
                $"Could not preserve the changed {record.Root}/{record.Path}: {ex.Message}. " +
                "It was left in place.");
        }
    }

    private async Task WriteLoadOrderAsync(
        GameInstallation installation,
        IGameModule module,
        DeploymentPlan plan,
        DeploymentResult result,
        CancellationToken ct)
    {
        var mods = plan.ModOrder
            .Select((modId, index) => new DeployedMod
            {
                ModId = modId,
                OrderIndex = index,
                Enabled = true,
                DeployedFiles = plan.Files
                    .Where(f => string.Equals(f.ModId, modId, StringComparison.OrdinalIgnoreCase))
                    .Select(f => new StagedFile(f.RootName, f.RelativePath, f.PackageType))
                    .ToList()
            })
            .ToList();

        result.DeployedMods = mods;

        try
        {
            await module.WriteLoadOrderAsync(installation, mods, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Could not write the load-order manifest: {ex.Message}");
            _logger?.LogWarning(ex, "WriteLoadOrderAsync failed for {ModuleId}", module.ModuleId);
        }
    }

    private void CollectLaunchRequirements(
        GameInstallation installation,
        IGameModule module,
        DeploymentPlan plan,
        DeploymentResult result)
    {
        try
        {
            result.LaunchRequirements =
                module.GetLaunchRequirements(installation, result.DeployedMods);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex, "GetLaunchRequirements failed for {ModuleId}", module.ModuleId);
            result.LaunchRequirements = LaunchRequirements.None;
        }

        // A proxy DLL next to a game executable only loads under Proton with a
        // native-first override, and a missing override is the most common cause of
        // "installed but nothing happened".
        var detected = ProxyDllDetector.Detect(installation, plan);
        if (detected.Count == 0)
            return;

        result.WineDllOverrides = detected
            .Concat(result.LaunchRequirements.WineDllOverrides)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    private static void TryRemoveEmptyDirectory(string? directory, string stopAt)
    {
        var current = directory;
        var boundary = Path.GetFullPath(stopAt).TrimEnd(Path.DirectorySeparatorChar);

        while (!string.IsNullOrEmpty(current))
        {
            var full = Path.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar);

            if (full.Length <= boundary.Length ||
                !full.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(full) ||
                Directory.EnumerateFileSystemEntries(full).Any())
            {
                return;
            }

            try
            {
                Directory.Delete(full);
            }
            catch (IOException)
            {
                return;
            }

            current = Path.GetDirectoryName(full);
        }
    }

    private IReadOnlyList<DeployConstraint> SafeDeployConstraints(IGameModule module)
    {
        try
        {
            return module.GetDeployConstraints();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex, "Game module {ModuleId} threw listing deploy constraints", module.ModuleId);
            return Array.Empty<DeployConstraint>();
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
}

/// <summary>
/// Thrown when a deployment aborts because the configured fallback is
/// <see cref="DeploymentFallback.Fail"/>.
/// </summary>
public sealed class DeploymentAbortedException : Exception
{
    /// <summary>
    /// Creates the exception.
    /// </summary>
    /// <param name="message">Why the deployment aborted.</param>
    /// <param name="inner">The failure that triggered it.</param>
    public DeploymentAbortedException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Where staging, backups, overwrite stores and deployment manifests live on disk.
/// </summary>
/// <remarks>
/// Shared by the staging manager and the deployer so both agree on the layout without
/// either owning the other.
/// </remarks>
public sealed class StagingLayout
{
    /// <summary>
    /// Creates a layout.
    /// </summary>
    /// <param name="basePath">Root of the staging areas.</param>
    /// <param name="backupPath">
    /// Root of the vanilla backup store. Defaults to a <c>backups</c> directory beside
    /// <paramref name="basePath"/>.
    /// </param>
    public StagingLayout(string basePath, string? backupPath = null)
    {
        BasePath = basePath;
        BackupBasePath = backupPath ?? Path.Combine(
            Path.GetDirectoryName(basePath.TrimEnd(Path.DirectorySeparatorChar))
                ?? basePath,
            "backups");
    }

    /// <summary>Root of the staging areas.</summary>
    public string BasePath { get; }

    /// <summary>Root of the vanilla backup store.</summary>
    public string BackupBasePath { get; }

    /// <summary>
    /// Staging directory for one game.
    /// </summary>
    /// <param name="gameKey">Game key.</param>
    /// <returns>The absolute path.</returns>
    public string GetGameStagingDirectory(string gameKey) =>
        Path.Combine(BasePath, gameKey);

    /// <summary>
    /// Store for files displaced by a user edit.
    /// </summary>
    /// <param name="gameKey">Game key.</param>
    /// <returns>The absolute path.</returns>
    public string GetOverwriteDirectory(string gameKey) =>
        Path.Combine(GetGameStagingDirectory(gameKey), "_overwrite");

    /// <summary>
    /// Vanilla backup store for one game build.
    /// </summary>
    /// <param name="gameKey">Game key.</param>
    /// <param name="buildId">Store build id, or <c>null</c> when unknown.</param>
    /// <returns>The absolute path.</returns>
    /// <remarks>
    /// Backups are stamped with the build they were taken from, so an update cannot cause
    /// a stale vanilla file to be restored over a newer one.
    /// </remarks>
    public string GetBackupDirectory(string gameKey, string? buildId) =>
        Path.Combine(BackupBasePath, gameKey, string.IsNullOrEmpty(buildId) ? "unknown" : buildId);
}
