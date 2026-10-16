using System.Text.Json;
using Microsoft.Extensions.Logging;
using Modular.Core.Archives;
using Modular.Core.Database;
using Modular.Core.Configuration;
using Modular.Core.Dependencies;
using Modular.Core.Deployment;
using Modular.Core.GameDetection;
using Modular.Core.Metadata;
using Modular.Core.Modules;
using Modular.Core.Plugins;
using Modular.Core.Snapshots;
using Modular.Core.Telemetry;
using Modular.Core.Utilities;
using Modular.Sdk.Installers;
using Modular.Sdk.Modules;

namespace Modular.Core.Installers;

/// <summary>
/// Orchestrates the full mod installation lifecycle:
/// archive registration → installer selection → dependency resolution →
/// staging → conflict detection → atomic commit → rollback support.
/// </summary>
public class ModInstallationService
{
    private readonly InstallerManager _installerManager;
    private readonly StagingManager _stagingManager;
    private readonly ChangesetManager _changesetManager;
    private readonly ArchiveInventoryService _archiveInventory;
    private readonly FileConflictIndex _conflictIndex;
    private readonly ModularDatabase _database;
    private readonly TelemetryService? _telemetry;
    private readonly SnapshotManager? _snapshotManager;
    private readonly GameModuleRegistry _moduleRegistry;
    private readonly GameInstallationResolver _gameResolver;
    private readonly IModCategorySource? _categorySource;
    private readonly DeploymentService _deployment;
    private readonly ILogger<ModInstallationService>? _logger;

    /// <summary>
    /// Creates the installation service.
    /// </summary>
    /// <param name="database">Modular database.</param>
    /// <param name="telemetry">Optional telemetry sink.</param>
    /// <param name="snapshotManager">Optional snapshot manager for auto-snapshots.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="pluginLoader">
    /// Optional plugin loader. When supplied, plugin-provided installers and game
    /// modules are registered, which is what finally makes them reachable from a
    /// real install.
    /// </param>
    /// <param name="moduleRegistry">Module registry; a default one is created when omitted.</param>
    /// <param name="gameResolver">
    /// Game installation resolver; a default one wired to this database is created
    /// when omitted.
    /// </param>
    /// <param name="stagingBasePath">
    /// Root of the staging areas. Defaults to <c>~/.config/Modular/staging</c>.
    /// </param>
    /// <param name="settings">
    /// App settings, for the configured deployment method and fallback.
    /// </param>
    /// <param name="deployment">
    /// Deployment service; a default one wired to this database is created when omitted.
    /// </param>
    /// <param name="categorySource">
    /// Mod hub categories. Looked up when a mod is installed, so that game modules which
    /// order mods by kind have the answer when they plan a deployment.
    /// </param>
    public ModInstallationService(
        ModularDatabase database,
        TelemetryService? telemetry = null,
        SnapshotManager? snapshotManager = null,
        ILogger<ModInstallationService>? logger = null,
        PluginLoader? pluginLoader = null,
        GameModuleRegistry? moduleRegistry = null,
        GameInstallationResolver? gameResolver = null,
        string? stagingBasePath = null,
        AppSettings? settings = null,
        DeploymentService? deployment = null,
        IModCategorySource? categorySource = null)
    {
        _database = database;
        _telemetry = telemetry;
        _snapshotManager = snapshotManager;
        _logger = logger;

        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "Modular");
        var stagingPath = stagingBasePath ?? Path.Combine(configDir, "staging");

        var archiveReaderFactory = new ArchiveReaderFactory();

        _installerManager = new InstallerManager(
            archiveReaderFactory: archiveReaderFactory,
            telemetry: telemetry);
        _stagingManager = new StagingManager(stagingPath);
        _changesetManager = new ChangesetManager(database);
        _archiveInventory = new ArchiveInventoryService(database);
        _conflictIndex = new FileConflictIndex();

        _categorySource = categorySource;
        _moduleRegistry = moduleRegistry
            ?? new GameModuleRegistry(archiveReaderFactory, categories: categorySource);
        _gameResolver = gameResolver ?? new GameInstallationResolver(
            registry: _moduleRegistry, database: database);

        _deployment = deployment ?? new DeploymentService(
            database, _stagingManager, _gameResolver, settings);

        if (pluginLoader != null)
            RegisterPluginContributions(pluginLoader);
    }

    /// <summary>
    /// Registers the installers and game modules a plugin loader has discovered.
    /// </summary>
    /// <param name="pluginLoader">The loader whose plugins have already been loaded.</param>
    /// <remarks>
    /// Plugin installers were previously discovered but never reached the
    /// <see cref="InstallerManager"/>, so they could not be selected for an install.
    /// </remarks>
    public void RegisterPluginContributions(PluginLoader pluginLoader)
    {
        ArgumentNullException.ThrowIfNull(pluginLoader);

        var installers = pluginLoader.GetAllInstallers();
        if (installers.Count > 0)
        {
            _installerManager.RegisterInstallers(installers);
            _logger?.LogInformation(
                "Registered {Count} plugin installer(s)", installers.Count);
        }

        var modules = pluginLoader.GetAllModules();
        if (modules.Count > 0)
        {
            _moduleRegistry.RegisterAll(modules);
            _logger?.LogInformation(
                "Registered {Count} plugin game module(s)", modules.Count);
        }
    }

    /// <summary>
    /// The module registry this service resolves games against.
    /// </summary>
    public GameModuleRegistry ModuleRegistry => _moduleRegistry;

    /// <summary>
    /// Installs a mod archive to a target game directory with full lifecycle tracking.
    /// </summary>
    public async Task<ModInstallationResult> InstallAsync(
        string archivePath,
        string targetDirectory,
        ModInstallationOptions? options = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new ModInstallationOptions();
        var result = new ModInstallationResult { ArchivePath = archivePath, TargetDirectory = targetDirectory };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Validate inputs
        if (!File.Exists(archivePath))
        {
            result.Error = $"Archive not found: {archivePath}";
            return result;
        }

        if (!Directory.Exists(targetDirectory))
        {
            result.Error = $"Target directory not found: {targetDirectory}";
            return result;
        }

        // A bare .pak is read through a temporary zip. The changeset keeps the real path,
        // so a reinstall or snapshot restore goes back to the file the user gave.
        LooseModFile? looseFile;
        try
        {
            looseFile = LooseModFile.WrapIfNeeded(archivePath);
        }
        catch (IOException ex)
        {
            result.Error = $"Could not prepare {Path.GetFileName(archivePath)}: {ex.Message}";
            return result;
        }

        using var looseFileScope = looseFile;
        var readPath = looseFile?.ArchivePath ?? archivePath;

        // Step 0: Resolve the game and the module that owns it.
        //
        // Two bugs die here. Installer selection used to run with no game id at all, so
        // SupportedGameIds scoping never applied to a real install; and nothing ever
        // wrote detected_games, so the auto-snapshot below could never find a game and
        // silently did nothing.
        progress?.Report(new InstallProgress { CurrentOperation = "Resolving game..." });

        var installation = await ResolveInstallationAsync(targetDirectory, options, ct);
        var module = installation != null
            ? _gameResolver.GetModule(installation)
            : _moduleRegistry.Resolve(options.GameId, null, targetDirectory);
        var gameId = options.GameId ?? installation?.GameId;
        var gameKey = installation?.GameKey ?? DeriveGameKey(targetDirectory);

        result.GameId = gameId;
        result.ModuleId = module?.ModuleId;

        var modPaths = installation != null && module != null
            ? module.ResolveModPaths(installation)
            : null;

        if (installation != null)
        {
            _logger?.LogInformation(
                "Installing for {Game} via module {ModuleId}",
                installation, module?.ModuleId ?? GenericGameModule.Id);
        }

        var staged = options.UseStaging;
        var modId = options.ModId ?? Path.GetFileNameWithoutExtension(archivePath);

        // Step 1: Create changeset record
        var changesetId = await _changesetManager.CreateChangesetAsync(
            modId: options.ModId,
            archivePath: archivePath,
            targetDirectory: targetDirectory,
            managedBy: staged ? ChangesetOwnership.Deployment : ChangesetOwnership.Legacy,
            gameKey: staged ? gameKey : null,
            ct: ct);
        result.ChangesetId = changesetId;
        result.ManagedBy = staged ? ChangesetOwnership.Deployment : ChangesetOwnership.Legacy;
        result.GameKey = gameKey;
        _logger?.LogInformation("Created changeset {Id} for {Archive}", changesetId, Path.GetFileName(archivePath));

        StagingSession? stagingSession = null;
        string? supersededChangesetId = null;

        try
        {
            // Step 2: Register archive in database
            await _changesetManager.UpdateStateAsync(changesetId, ChangesetState.Staging, ct: ct);
            progress?.Report(new InstallProgress { CurrentOperation = "Scanning archive..." });

            var inventory = await _archiveInventory.GetInventoryAsync(readPath, ct);
            _logger?.LogInformation("Archive contains {Count} entries", inventory.Count);

            // Step 3: Select installer, scoped to the resolved game and module
            progress?.Report(new InstallProgress { CurrentOperation = "Detecting installer type..." });
            var selection = await _installerManager.SelectInstallerAsync(
                readPath, gameId, module, ct);

            if (selection == null)
            {
                result.Error = "No suitable installer found for this archive";
                await _changesetManager.UpdateStateAsync(changesetId, ChangesetState.Failed, ct: ct);
                return result;
            }

            _logger?.LogInformation("Selected installer: {Name} (confidence: {Confidence:P0})",
                selection.Installer.DisplayName, selection.DetectionResult.Confidence);
            result.InstallerUsed = selection.Installer.DisplayName;

            // Step 4: Create install plan
            progress?.Report(new InstallProgress { CurrentOperation = "Analyzing mod structure..." });
            var context = new InstallContext
            {
                GameDirectory = targetDirectory,
                GameId = gameId,
                ModId = options.ModId,
                AllowOverwrite = options.AllowOverwrite,
                CreateBackups = options.CreateBackups,
                ConflictPolicy = options.AllowOverwrite ? ConflictPolicy.LastWriterWins : ConflictPolicy.FailOnConflict,
                Game = installation,
                ModPaths = modPaths
            };

            foreach (var (choiceId, optionId) in options.Selections)
                context.Selections[choiceId] = optionId;

            var plan = await _installerManager.CreateInstallPlanAsync(
                readPath, context, preferredInstaller: selection.Installer, module: module, ct: ct);

            // Step 4b: Settle anything the archive leaves to the user.
            //
            // The plan already assumes a default for each choice, so an install with
            // nobody to ask still goes ahead; the assumption is reported, not hidden.
            var open = plan.Choices.Where(c => !c.IsResolved).ToList();
            if (open.Count > 0 && !options.DryRun)
            {
                if (options.ChoiceResolver != null)
                {
                    var answers = await options.ChoiceResolver(plan.Choices, ct);
                    if (answers == null)
                    {
                        result.Cancelled = true;
                        result.Error = "Installation cancelled";
                        await _changesetManager.UpdateStateAsync(changesetId, ChangesetState.RolledBack, ct: ct);
                        return result;
                    }

                    foreach (var (choiceId, optionId) in answers)
                        context.Selections[choiceId] = optionId;

                    plan = await _installerManager.CreateInstallPlanAsync(
                        readPath, context, preferredInstaller: selection.Installer, module: module, ct: ct);
                }

                foreach (var choice in plan.Choices.Where(c => !c.IsResolved))
                {
                    var assumed = choice.Options.FirstOrDefault(o => o.Id == choice.SelectedOptionId);
                    result.Warnings.Add(
                        $"{choice.Title} Nothing was chosen, so '{assumed?.Label ?? choice.SelectedOptionId}' " +
                        $"was installed out of {choice.Options.Count} options.");
                }
            }

            result.Choices = plan.Choices;

            // Step 4c: Learn the mod's category while there is time to ask.
            await WarmCategoryAsync(archivePath, module, ct);

            if (options.DryRun)
            {
                result.Success = true;
                result.DryRun = true;
                result.PlannedOperations = plan.Operations.Select(o => o.DestinationPath).ToList();
                await _changesetManager.UpdateStateAsync(changesetId, ChangesetState.RolledBack, ct: ct);
                return result;
            }

            // Step 5: Check for file conflicts — legacy direct installs only.
            //
            // For a staged install this check is worse than useless: every file Modular
            // itself deployed already exists in the game folder, so each reinstall or
            // redeploy would report conflicts with its own output. Overlaps between
            // mods come from the deployment planner instead, which knows which files
            // Modular owns.
            if (!staged)
            {
                progress?.Report(new InstallProgress { CurrentOperation = "Checking for conflicts..." });
                foreach (var op in plan.Operations.Where(o => o.Type == FileOperationType.Extract || o.Type == FileOperationType.Copy))
                {
                    var destPath = Path.Combine(targetDirectory, op.DestinationPath);
                    if (File.Exists(destPath) && !options.AllowOverwrite)
                    {
                        _conflictIndex.RegisterFile(op.DestinationPath, options.ModId ?? "current", op.SourcePath, null);
                    }
                }
            }

            // Step 5b: Redirect the plan into this mod's staging tree.
            //
            // After this the installer writes nothing into the game folder. Paths are
            // normalised here, once, into (root, root-relative path) pairs, so no later
            // stage has to know that an installer targeted a subdirectory of the game.
            StagingRedirection? redirection = null;
            if (staged)
            {
                stagingSession = _stagingManager.CreateSession(changesetId, gameKey, modId);
                redirection = _stagingManager.RedirectPlan(
                    plan, targetDirectory, stagingSession.StagingDirectory);

                result.StagingPath = stagingSession.StagingDirectory;
            }

            // Step 6: Sort operations by dependencies (DAG)
            var sortedOps = OperationGraph.Sort(plan.Operations);
            if (sortedOps == null)
            {
                result.Error = "Circular dependency detected in file operations";
                await _changesetManager.UpdateStateAsync(changesetId, ChangesetState.Failed, ct: ct);
                return result;
            }
            plan.Operations = sortedOps;

            // Step 7: Stage and install
            await _changesetManager.UpdateStateAsync(changesetId, ChangesetState.Ready, ct: ct);

            progress?.Report(new InstallProgress
            {
                CurrentOperation = "Installing files...",
                TotalFiles = plan.Operations.Count
            });

            await _changesetManager.UpdateStateAsync(changesetId, ChangesetState.Committing, ct: ct);

            var installResult = await _installerManager.ExecuteInstallAsync(plan, progress, ct);

            if (!installResult.Success)
            {
                result.Error = installResult.Error;
                await _changesetManager.UpdateStateAsync(changesetId, ChangesetState.Failed, ct: ct);
                return result;
            }

            // Step 7b: Validate the staged layout, then place the files.
            if (staged && stagingSession != null && redirection != null)
            {
                // Labels come from the redirection: the staged tree on disk knows paths,
                // not what kind of package each file is or which rule routed it.
                var stagedMod = StagingManager.BuildStagedMod(
                    modId, stagingSession.StagingDirectory, redirection.NormalizedFiles);

                result.StagedFiles = stagedMod.Files
                    .Select(f => $"{f.RootName}/{f.RelativePath}")
                    .ToList();

                if (installation != null && module != null)
                {
                    var diagnostics = SafeValidateStaged(module, installation, stagedMod);

                    result.Diagnostics.AddRange(diagnostics);

                    var errors = diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .ToList();

                    if (errors.Count > 0)
                    {
                        // An invalid layout must not reach the game folder, and the
                        // staged tree is kept so the user can inspect it.
                        result.Error =
                            "Staged layout rejected: " +
                            string.Join("; ", errors.Select(e => $"{e.Code}: {e.Message}"));
                        await _changesetManager.UpdateStateAsync(
                            changesetId, ChangesetState.Failed, ct: ct);
                        return result;
                    }
                }

                // Step 7c: Register the mod in the game's deployment, then deploy the
                // whole ordered set. Registering before deploying is what makes the
                // deployment the single source of truth for what is in the game folder.
                var effectiveInstallation = installation ?? new GameInstallation
                {
                    GameId = gameId ?? string.Empty,
                    InstallPath = targetDirectory,
                    ModuleId = module?.ModuleId ?? GenericGameModule.Id,
                    DisplayName = new DirectoryInfo(targetDirectory).Name
                };

                // A reinstall takes over the mod's row and staging folder, so whichever
                // changeset held them before is retired once this one commits.
                var previous = await _deployment.Mods.GetAsync(gameKey, modId, ct);
                if (previous?.ChangesetId != null && previous.ChangesetId != changesetId)
                    supersededChangesetId = previous.ChangesetId;

                await _deployment.RegisterModAsync(new GameModRecord
                {
                    GameKey = gameKey,
                    ModId = modId,
                    ModuleId = module?.ModuleId,
                    InstallerId = selection.Installer.InstallerId,
                    StagingPath = stagingSession.StagingDirectory,
                    DisplayName = options.ModId,
                    Source = options.Source,
                    SourceFileId = options.SourceFileId,
                    ChangesetId = changesetId
                }, ct);

                if (options.NoDeploy)
                {
                    result.Deployed = false;
                    installResult.InstalledFiles = new List<string>();
                    installResult.BackedUpFiles = new List<string>();
                }
                else
                {
                    progress?.Report(new InstallProgress { CurrentOperation = "Deploying..." });

                    var deployment = await _deployment.DeployAsync(
                        effectiveInstallation, ct: ct);

                    if (!deployment.Success)
                    {
                        result.Error = deployment.Error ?? "Deployment failed";
                        await _changesetManager.UpdateStateAsync(
                            changesetId, ChangesetState.Failed, ct: ct);
                        return result;
                    }

                    result.Deployed = true;
                    result.Deployment = deployment;
                    result.Warnings.AddRange(deployment.Warnings);

                    installResult.InstalledFiles = deployment.Manifest?.Files
                        .Where(f => string.Equals(f.ModId, modId, StringComparison.OrdinalIgnoreCase))
                        .Select(f => deployment.Manifest!.Roots.TryGetValue(f.Root, out var root)
                            ? Path.Combine(root, f.Path.Replace('/', Path.DirectorySeparatorChar))
                            : f.Path)
                        .ToList() ?? new List<string>();
                    installResult.BackedUpFiles = deployment.VanillaBackedUp.ToList();
                }
            }

            // Step 8: Record operations for rollback.
            //
            // A staged changeset records its staged paths *as staged paths*, so they can
            // never be mistaken for game paths and deleted out from under a deployment,
            // plus the deployed paths the temporary single-mod deploy actually wrote.
            var operationsData = JsonSerializer.Serialize(new
            {
                managedBy = staged ? "deployment" : "legacy",
                gameKey = staged ? gameKey : null,
                stagingPath = stagingSession?.StagingDirectory,
                stagedFiles = result.StagedFiles,
                deployedFiles = staged ? installResult.InstalledFiles : null,
                installedFiles = staged ? new List<string>() : installResult.InstalledFiles,
                backedUpFiles = installResult.BackedUpFiles,
                manifest = installResult.Manifest
            });
            await _changesetManager.UpdateStateAsync(changesetId, ChangesetState.Committed, operationsData, ct);

            // Left committed, the replaced changeset is listed as a second copy of the mod,
            // and undoing it would delete files and staging that are now this one's.
            if (supersededChangesetId != null)
            {
                await _changesetManager.UpdateStateAsync(
                    supersededChangesetId, ChangesetState.Superseded, ct: ct);
            }

            // Step 9: Record telemetry
            stopwatch.Stop();
            _telemetry?.RecordInstallerResult(
                selection.Installer.InstallerId,
                true,
                stopwatch.Elapsed);

            result.Success = true;
            result.InstalledFiles = installResult.InstalledFiles;
            result.BackedUpFiles = installResult.BackedUpFiles;

            _logger?.LogInformation(
                "Installation complete: {Count} files installed in {Duration:F1}s",
                installResult.InstalledFiles.Count, stopwatch.Elapsed.TotalSeconds);

            // Auto-snapshot after a successful deploy. An install that deployed nothing
            // changed nothing in the game folder, so there is no new state to capture.
            if (options.AutoSnapshot && _snapshotManager != null && !options.NoDeploy)
            {
                try
                {
                    // The resolved installation is authoritative; the table lookup
                    // is the fallback for callers that skipped resolution.
                    var appId = installation?.SteamAppId;
                    var name = installation?.DisplayName;

                    if (appId == null)
                    {
                        var gameInfo = await TryDetectGameForPathAsync(targetDirectory, ct);
                        appId = gameInfo?.appId;
                        name ??= gameInfo?.name;
                    }

                    if (appId != null)
                    {
                        await _snapshotManager.CreateSnapshotAsync(
                            appId.Value, name ?? "Unknown Game", targetDirectory,
                            SnapshotTrigger.AutoInstall,
                            name: $"Auto — {options.ModId ?? Path.GetFileName(archivePath)} installed",
                            ct: ct,
                            gameKey: gameKey);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Auto-snapshot failed after install (non-fatal)");
                }
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            result.Error = "Installation cancelled";
            result.Cancelled = true;
            await _changesetManager.UpdateStateAsync(changesetId, ChangesetState.Failed, ct: CancellationToken.None);
            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            await _changesetManager.UpdateStateAsync(changesetId, ChangesetState.Failed, ct: CancellationToken.None);
            _logger?.LogError(ex, "Installation failed for {Archive}", archivePath);
            _telemetry?.RecordInstallerResult("unknown", false, stopwatch.Elapsed);
            return result;
        }
    }

    /// <summary>
    /// Uninstalls a previously installed changeset by restoring backups and removing installed files.
    /// </summary>
    public async Task<ModUninstallResult> UninstallAsync(
        string changesetId,
        CancellationToken ct = default)
    {
        var result = new ModUninstallResult { ChangesetId = changesetId };

        var changeset = await _changesetManager.GetChangesetAsync(changesetId, ct);
        if (changeset == null)
        {
            result.Error = $"Changeset '{changesetId}' not found";
            return result;
        }

        if (changeset.State != ChangesetState.Committed)
        {
            result.Error = $"Changeset '{changesetId}' is in state '{changeset.State}', expected 'Committed'";
            return result;
        }

        if (string.IsNullOrEmpty(changeset.OperationsJson) || string.IsNullOrEmpty(changeset.TargetDirectory))
        {
            result.Error = "Changeset missing operations data or target directory";
            return result;
        }

        try
        {
            // A managed changeset is undone through the deployment, not by deleting
            // paths: its operations record staged paths, and the deployment owns what is
            // in the game folder.
            if (changeset.ManagedBy == ChangesetOwnership.Deployment)
            {
                // A replaced changeset would fall through to the recorded paths below and
                // delete the files and staging its replacement now owns.
                if ((await FindSupersededAsync([changeset], ct)).Count > 0)
                {
                    await _changesetManager.UpdateStateAsync(
                        changesetId, ChangesetState.Superseded, ct: ct);
                    result.Error =
                        $"Changeset '{changesetId}' was replaced by a later install of the same mod; " +
                        "uninstall that one instead.";
                    return result;
                }

                var managed = await UninstallManagedAsync(changeset, result, ct);
                if (managed)
                {
                    await _changesetManager.UpdateStateAsync(
                        changesetId, ChangesetState.RolledBack, ct: ct);
                    return result;
                }

                // No game_mod row: a changeset from the phase where staged installs were
                // still placed file by file. Fall through to the recorded paths.
            }

            var ops = JsonSerializer.Deserialize<JsonElement>(changeset.OperationsJson);

            // A managed changeset records staged paths, never game paths. Its
            // game-folder files are named by "deployedFiles"; deleting the staged paths
            // would remove the deployable copies and leave the game folder modded.
            var isManaged = changeset.ManagedBy == ChangesetOwnership.Deployment;

            var pathsToRemove = ReadStringArray(
                ops, isManaged ? "deployedFiles" : "installedFiles");

            var removedCount = 0;
            var restoredCount = 0;

            foreach (var file in pathsToRemove)
            {
                // Managed paths are absolute; legacy paths may be either.
                var fullPath = Path.IsPathRooted(file)
                    ? file
                    : Path.Combine(changeset.TargetDirectory, file);

                if (File.Exists(fullPath))
                {
                    // Delete, never truncate. A deployed file may be a hardlink or a
                    // symlink sharing its bytes with the staged copy, and File.Delete
                    // removes the link itself rather than writing through it.
                    File.Delete(fullPath);
                    removedCount++;
                }

                // Restore a vanilla backup under either suffix. The installers write
                // ".modular.bak" and the staging commit writes ".backup"; restoring only
                // one of them left the other orphaned and the game file missing.
                foreach (var suffix in BackupSuffixes)
                {
                    var backupPath = fullPath + suffix;
                    if (!File.Exists(backupPath))
                        continue;

                    if (File.Exists(fullPath))
                        File.Delete(fullPath);

                    File.Move(backupPath, fullPath);
                    restoredCount++;
                    break;
                }
            }

            // Clean up empty directories (bottom-up)
            var dirs = pathsToRemove
                .Select(f => Path.GetDirectoryName(
                    Path.IsPathRooted(f) ? f : Path.Combine(changeset.TargetDirectory, f)))
                .Where(d => d != null)
                .Distinct()
                .OrderByDescending(d => d!.Length)
                .ToList();

            foreach (var dir in dirs)
            {
                if (Directory.Exists(dir!) &&
                    !Directory.EnumerateFileSystemEntries(dir!).Any() &&
                    dir != changeset.TargetDirectory)
                {
                    Directory.Delete(dir!);
                }
            }

            // The staging tree goes only after the game folder is clean, so a failure
            // above leaves a recoverable state.
            if (isManaged)
            {
                var stagingPath = ops.TryGetProperty("stagingPath", out var sp) &&
                                  sp.ValueKind == JsonValueKind.String
                    ? sp.GetString()
                    : null;

                if (!string.IsNullOrEmpty(stagingPath) && Directory.Exists(stagingPath))
                {
                    Directory.Delete(stagingPath, recursive: true);
                    result.StagingRemoved = true;
                }
            }

            await _changesetManager.UpdateStateAsync(changesetId, ChangesetState.RolledBack, ct: ct);

            result.Success = true;
            result.FilesRemoved = removedCount;
            result.FilesRestored = restoredCount;

            _logger?.LogInformation(
                "Uninstall complete: {Removed} files removed, {Restored} backups restored",
                removedCount, restoredCount);

            // Auto-snapshot after successful uninstall
            if (_snapshotManager != null && changeset.TargetDirectory != null)
            {
                try
                {
                    var gameInfo = await TryDetectGameForPathAsync(changeset.TargetDirectory, ct);
                    if (gameInfo != null)
                    {
                        await _snapshotManager.CreateSnapshotAsync(
                            gameInfo.Value.appId, gameInfo.Value.name, changeset.TargetDirectory,
                            SnapshotTrigger.AutoUninstall,
                            name: $"Auto — {changeset.ModId ?? changesetId} uninstalled",
                            ct: ct,
                            gameKey: changeset.GameKey);
                    }
                }
                catch (Exception ex2)
                {
                    _logger?.LogWarning(ex2, "Auto-snapshot failed after uninstall (non-fatal)");
                }
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            _logger?.LogError(ex, "Uninstall failed for changeset {Id}", changesetId);
        }

        return result;
    }

    /// <summary>
    /// The deployment service behind this install service.
    /// </summary>
    public DeploymentService Deployment => _deployment;

    /// <summary>
    /// Deploys a game's whole ordered mod set.
    /// </summary>
    /// <param name="gameRef">AppID, slug, display name or path of the game.</param>
    /// <param name="method">Deployment method for this run only.</param>
    /// <param name="loadOrder">An explicit load order, normally a profile's.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What the deployment did.</returns>
    public Task<Deployment.DeploymentResult> DeployGameAsync(
        string gameRef,
        Deployment.DeploymentMethod? method = null,
        IReadOnlyList<string>? loadOrder = null,
        CancellationToken ct = default) =>
        _deployment.DeployAsync(gameRef, method, loadOrder, ct);

    /// <summary>
    /// Removes a managed mod from its game's deployment and redeploys.
    /// </summary>
    /// <param name="changeset">The changeset being undone.</param>
    /// <param name="result">Result to fill in.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// True when the mod was found in a deployment and removed; false when there is no
    /// <c>game_mod</c> row, so the caller should fall back to the recorded paths.
    /// </returns>
    private async Task<bool> UninstallManagedAsync(
        ChangesetRecord changeset,
        ModUninstallResult result,
        CancellationToken ct)
    {
        var record = await _deployment.Mods.GetByChangesetAsync(changeset.ChangesetId, ct);
        if (record == null)
            return false;

        var installation = await ResolveInstallationForKeyAsync(
            record.GameKey, changeset.TargetDirectory, ct);

        if (installation == null)
        {
            result.Error =
                $"Cannot uninstall '{record.ModId}': the game for key '{record.GameKey}' " +
                "could not be resolved. Pass its install path or reinstall the game.";
            return true;
        }

        var deployment = await _deployment.RemoveModAsync(installation, record.ModId, ct);

        if (!deployment.Success)
        {
            // Staging is intact and the mod is back in the set, so the state is recoverable.
            result.Error = deployment.Error ?? "Redeploy after removal failed";
            return true;
        }

        result.Success = true;
        result.FilesRemoved = deployment.Removed.Count;
        result.FilesRestored = deployment.Restored.Count;
        result.StagingRemoved = deployment.StagingRemoved.Count > 0;
        result.Deployment = deployment;

        _logger?.LogInformation(
            "Removed {ModId} from {GameKey}: {Removed} files removed, {Restored} restored",
            record.ModId, record.GameKey, deployment.Removed.Count, deployment.Restored.Count);

        return true;
    }

    /// <summary>
    /// Removes one mod from a game's deployment by name.
    /// </summary>
    /// <param name="gameRef">AppID, slug, display name or path of the game.</param>
    /// <param name="modId">Mod to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What was removed.</returns>
    /// <remarks>
    /// The same code path as <see cref="UninstallAsync"/> for a managed mod, so
    /// <c>uninstall --game g --mod m</c> and <c>uninstall &lt;changesetId&gt;</c> cannot
    /// diverge.
    /// </remarks>
    public async Task<ModUninstallResult> UninstallModAsync(
        string gameRef,
        string modId,
        CancellationToken ct = default)
    {
        var result = new ModUninstallResult { ChangesetId = string.Empty };

        var installation = await _gameResolver.ResolveAsync(gameRef, persist: false, ct);
        if (installation == null)
        {
            result.Error = $"Could not resolve game '{gameRef}'.";
            return result;
        }

        var record = await _deployment.Mods.GetAsync(installation.GameKey, modId, ct);
        if (record == null)
        {
            result.Error =
                $"'{modId}' is not part of the deployment for {installation.DisplayName ?? gameRef}.";
            return result;
        }

        result.ChangesetId = record.ChangesetId ?? string.Empty;

        var deployment = await _deployment.RemoveModAsync(installation, modId, ct);

        if (!deployment.Success)
        {
            result.Error = deployment.Error ?? "Redeploy after removal failed";
            return result;
        }

        if (record.ChangesetId != null)
        {
            await _changesetManager.UpdateStateAsync(
                record.ChangesetId, ChangesetState.RolledBack, ct: ct);
        }

        result.Success = true;
        result.FilesRemoved = deployment.Removed.Count;
        result.FilesRestored = deployment.Restored.Count;
        result.StagingRemoved = deployment.StagingRemoved.Count > 0;
        result.Deployment = deployment;
        return result;
    }

    /// <summary>
    /// Resolves the installation behind a game key.
    /// </summary>
    /// <param name="gameKey">Game key from the deployment tables.</param>
    /// <param name="knownPath">A directory recorded for the game, when one is known.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The installation, or <c>null</c> when it could not be resolved.</returns>
    private async Task<GameInstallation?> ResolveInstallationForKeyAsync(
        string gameKey, string? knownPath, CancellationToken ct)
    {
        // The recorded directory is the most reliable route: it is where the files
        // actually went.
        if (!string.IsNullOrEmpty(knownPath) && Directory.Exists(knownPath))
        {
            var byPath = await _gameResolver.ResolveAsync(knownPath, persist: false, ct);
            if (byPath != null)
                return byPath;
        }

        return await _gameResolver.ResolveAsync(gameKey, persist: false, ct);
    }

    // Asks the mod hub what kind of mod an archive is, when the archive can be traced to
    // one. Only ever an aid to ordering, so nothing here can fail an install.
    private async Task WarmCategoryAsync(string archivePath, IGameModule? module, CancellationToken ct)
    {
        if (_categorySource == null || module == null)
            return;

        var modId = NexusFileName.ParseModId(archivePath);
        var gameDomain = module.GameIds.FirstOrDefault(id => id.Length > 0 && !id.All(char.IsDigit));

        if (modId == null || gameDomain == null)
            return;

        try
        {
            await _categorySource.WarmAsync(gameDomain, [modId], ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "Category lookup failed for {Archive}", archivePath);
        }
    }

    /// <summary>
    /// Resolves the game an installed changeset belongs to, and the module that owns it.
    /// </summary>
    /// <param name="changeset">An installed changeset.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The installation and its module, or <c>null</c> when the game can no longer be found.
    /// </returns>
    public async Task<(GameInstallation Installation, IGameModule Module)?> ResolveGameAsync(
        ChangesetRecord changeset, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(changeset);

        GameInstallation? installation = null;

        if (!string.IsNullOrEmpty(changeset.GameKey))
        {
            installation = await ResolveInstallationForKeyAsync(
                changeset.GameKey, changeset.TargetDirectory, ct);
        }
        else if (!string.IsNullOrEmpty(changeset.TargetDirectory) &&
                 Directory.Exists(changeset.TargetDirectory))
        {
            installation = await _gameResolver.ResolveAsync(
                changeset.TargetDirectory, persist: false, ct);
        }

        return installation == null
            ? null
            : (installation, _gameResolver.GetModule(installation));
    }

    /// <summary>
    /// Lists all committed (installed) changesets, optionally filtered by target directory.
    /// </summary>
    public async Task<List<ChangesetRecord>> ListInstalledAsync(
        string? targetDirectory = null,
        CancellationToken ct = default)
    {
        var committed = await _changesetManager.ListByStateAsync(ChangesetState.Committed, ct);

        // Reinstalls made before superseding existed left their old changesets committed.
        var superseded = await FindSupersededAsync(committed, ct);
        foreach (var changesetId in superseded)
            await _changesetManager.UpdateStateAsync(changesetId, ChangesetState.Superseded, ct: ct);

        committed = committed.Where(c => !superseded.Contains(c.ChangesetId)).ToList();

        if (targetDirectory != null)
        {
            var fullTarget = Path.GetFullPath(targetDirectory);
            committed = committed.Where(c =>
                c.TargetDirectory != null &&
                Path.GetFullPath(c.TargetDirectory).Equals(fullTarget, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return committed;
    }

    /// <summary>
    /// Finds managed changesets whose mod was since reinstalled under a newer changeset.
    /// </summary>
    /// <param name="committed">Committed changesets.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The ids of the changesets that were replaced.</returns>
    /// <remarks>
    /// Staging is one folder per game and mod, so a changeset whose staging folder is now
    /// held by a game_mod row naming a different changeset has been replaced. A managed
    /// changeset with no row on its folder at all is from before deployments and is kept.
    /// </remarks>
    private async Task<HashSet<string>> FindSupersededAsync(
        IEnumerable<ChangesetRecord> committed, CancellationToken ct)
    {
        var superseded = new HashSet<string>(StringComparer.Ordinal);
        var ownerByStaging = new Dictionary<string, Dictionary<string, string?>>(StringComparer.OrdinalIgnoreCase);

        foreach (var changeset in committed)
        {
            if (changeset.ManagedBy != ChangesetOwnership.Deployment ||
                string.IsNullOrEmpty(changeset.GameKey) ||
                ReadStagingPath(changeset) is not { } stagingPath)
            {
                continue;
            }

            if (!ownerByStaging.TryGetValue(changeset.GameKey, out var owners))
            {
                var mods = await _deployment.Mods.ListAsync(changeset.GameKey, ct: ct);
                owners = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (var mod in mods.Where(m => !string.IsNullOrEmpty(m.StagingPath)))
                    owners[Path.GetFullPath(mod.StagingPath)] = mod.ChangesetId;
                ownerByStaging[changeset.GameKey] = owners;
            }

            if (owners.TryGetValue(Path.GetFullPath(stagingPath), out var owner) &&
                owner != null &&
                owner != changeset.ChangesetId)
            {
                superseded.Add(changeset.ChangesetId);
            }
        }

        return superseded;
    }

    private static string? ReadStagingPath(ChangesetRecord changeset)
    {
        if (string.IsNullOrEmpty(changeset.OperationsJson))
            return null;

        try
        {
            var ops = JsonSerializer.Deserialize<JsonElement>(changeset.OperationsJson);
            return ops.ValueKind == JsonValueKind.Object &&
                   ops.TryGetProperty("stagingPath", out var sp) &&
                   sp.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrEmpty(sp.GetString())
                ? sp.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves a Steam AppID or game name to an install directory.
    /// </summary>
    public static async Task<string?> ResolveGameDirectoryAsync(
        string gameIdentifier,
        CancellationToken ct = default)
    {
        var scanner = new SteamGameScanner(new SteamLibraryScanner(new SteamLocator()));
        var games = await scanner.ScanAllAsync(ct);

        // Try exact AppID match
        if (int.TryParse(gameIdentifier, out var appId))
        {
            var game = games.FirstOrDefault(g => g.AppId == appId);
            if (game != null)
                return game.InstallPath;
        }

        // Try case-insensitive name match
        var byName = games.FirstOrDefault(g =>
            g.DisplayName != null &&
            g.DisplayName.Contains(gameIdentifier, StringComparison.OrdinalIgnoreCase));

        return byName?.InstallPath;
    }

    /// <summary>
    /// Backup suffixes Modular has written, newest convention first.
    /// </summary>
    /// <remarks>
    /// The game-specific installers use <c>.modular.bak</c> and the universal ones use
    /// <c>.backup</c>. Uninstall used to restore only the latter, which silently lost
    /// the vanilla file for any Cyberpunk, FF7R or HZD install.
    /// </remarks>
    private static readonly string[] BackupSuffixes = [".backup", ".modular.bak"];

    private static List<string> ReadStringArray(JsonElement element, string propertyName)
    {
        var values = new List<string>();

        if (!element.TryGetProperty(propertyName, out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return values;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } value)
                values.Add(value);
        }

        return values;
    }

    /// <summary>
    /// Runs a module's staged-layout validation without letting it break the install.
    /// </summary>
    /// <param name="module">The module to ask.</param>
    /// <param name="installation">The resolved installation.</param>
    /// <param name="stagedMod">The staged mod.</param>
    /// <returns>The diagnostics, or none when the module threw.</returns>
    private IReadOnlyList<ModDiagnostic> SafeValidateStaged(
        IGameModule module, GameInstallation installation, StagedMod stagedMod)
    {
        var diagnostics = new List<ModDiagnostic>();

        try
        {
            diagnostics.AddRange(module.ValidateStaged(installation, stagedMod));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex, "Game module {ModuleId} threw during staged validation", module.ModuleId);
        }

        // Route-table checks run for every module, so a module that declares its
        // destinations as data gets them without writing them.
        diagnostics.AddRange(PackageRouteValidator.Validate(module, stagedMod));

        return diagnostics;
    }

    /// <summary>
    /// Derives a stable staging key for a target directory with no resolved game.
    /// </summary>
    /// <param name="targetDirectory">The game directory.</param>
    /// <returns>A filesystem-safe key.</returns>
    /// <remarks>
    /// Keyed on the full path so two unresolved games never share a staging area.
    /// </remarks>
    private static string DeriveGameKey(string targetDirectory)
    {
        var full = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(full));

        return "path-" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    /// <summary>
    /// Resolves the installation an install is targeting.
    /// </summary>
    /// <param name="targetDirectory">The game directory the caller supplied.</param>
    /// <param name="options">Installation options, which may name the game explicitly.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resolved installation, or <c>null</c> when nothing could be resolved.</returns>
    /// <remarks>
    /// Resolution walks up from the supplied directory, so pointing at
    /// <c>&lt;game&gt;/bin/x64</c> still resolves the real root. An explicit
    /// <see cref="ModInstallationOptions.GameId"/> is tried first, and its result is
    /// only accepted when it agrees with the directory being installed into —
    /// otherwise the directory wins, because that is where files will actually land.
    /// </remarks>
    private async Task<GameInstallation?> ResolveInstallationAsync(
        string targetDirectory,
        ModInstallationOptions options,
        CancellationToken ct)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(options.GameId))
            {
                var byId = await _gameResolver.ResolveAsync(options.GameId, persist: true, ct);
                if (byId != null && SameDirectory(byId.InstallPath, targetDirectory))
                    return byId;
            }

            return await _gameResolver.ResolveAsync(targetDirectory, persist: true, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Resolution is an enrichment step. A failure costs scoping and
            // auto-snapshots, not the install itself.
            _logger?.LogWarning(ex, "Game resolution failed for {Path}", targetDirectory);
            return null;
        }
    }

    private static bool SameDirectory(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return false;

        return string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tries to detect the game for a target directory by querying the detected_games table.
    /// </summary>
    private async Task<(int appId, string name)?> TryDetectGameForPathAsync(
        string targetDirectory, CancellationToken ct)
    {
        var connection = await _database.GetConnectionAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT steam_appid, display_name FROM detected_games
            WHERE install_path = @path
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@path", targetDirectory);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (await reader.ReadAsync(ct))
        {
            return (reader.GetInt32(0), reader.IsDBNull(1) ? "Unknown Game" : reader.GetString(1));
        }

        return null;
    }
}

/// <summary>
/// Options for mod installation.
/// </summary>
public class ModInstallationOptions
{
    /// <summary>Optional mod identifier for tracking.</summary>
    public string? ModId { get; set; }

    /// <summary>
    /// Game the archive is being installed for: a NexusMods slug, a Steam AppID or
    /// a GameBanana game id.
    /// </summary>
    /// <remarks>
    /// Scopes installer selection. When omitted, the game is resolved from the
    /// target directory instead.
    /// </remarks>
    public string? GameId { get; set; }

    /// <summary>Whether to overwrite existing files.</summary>
    public bool AllowOverwrite { get; set; }

    /// <summary>Whether to create backups of overwritten files.</summary>
    public bool CreateBackups { get; set; } = true;

    /// <summary>Show what would be installed without actually installing.</summary>
    public bool DryRun { get; set; }

    /// <summary>Whether to automatically create a snapshot after installation.</summary>
    public bool AutoSnapshot { get; set; } = true;

    /// <summary>
    /// Whether the install goes through a persistent per-mod staging tree.
    /// </summary>
    /// <remarks>
    /// The default. Setting this false takes the legacy path, which writes the archive
    /// straight into the game folder and keeps the old pre-install conflict check.
    /// </remarks>
    public bool UseStaging { get; set; } = true;

    /// <summary>
    /// Whether to stage the mod without deploying it.
    /// </summary>
    /// <remarks>
    /// The mod is registered in the game's mod set but nothing reaches the game folder
    /// until the next deploy. No auto-snapshot is taken, because nothing changed.
    /// </remarks>
    public bool NoDeploy { get; set; }

    /// <summary>Where the mod came from: <c>nexus</c>, <c>gamebanana</c> or <c>local</c>.</summary>
    public string Source { get; set; } = "local";

    /// <summary>
    /// Answers to the choices an archive offers, as choice id to option id, for a caller
    /// that already knows them.
    /// </summary>
    public Dictionary<string, string> Selections { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Asks the user to settle the choices an archive offers. Given every choice, with
    /// the installer's default marked on each; returns choice id to option id, or
    /// <c>null</c> to cancel the install. When absent, the defaults are installed and
    /// reported as warnings.
    /// </summary>
    public Func<IReadOnlyList<InstallChoice>, CancellationToken, Task<IReadOnlyDictionary<string, string>?>>? ChoiceResolver { get; set; }

    /// <summary>Backend file id, stored so an update or 1-Click flow can find it again.</summary>
    public string? SourceFileId { get; set; }
}

/// <summary>
/// Result of a mod installation operation.
/// </summary>
public class ModInstallationResult
{
    public bool Success { get; set; }
    public bool DryRun { get; set; }
    public bool Cancelled { get; set; }
    public string ArchivePath { get; set; } = string.Empty;
    public string TargetDirectory { get; set; } = string.Empty;
    public string? ChangesetId { get; set; }
    public string? InstallerUsed { get; set; }

    /// <summary>Game identifier that scoped installer selection, when one was resolved.</summary>
    public string? GameId { get; set; }

    /// <summary>Identifier of the game module that owned this install.</summary>
    public string? ModuleId { get; set; }

    /// <summary>Key of the game this install belongs to.</summary>
    public string? GameKey { get; set; }

    /// <summary>Who owns the installed files.</summary>
    public ChangesetOwnership ManagedBy { get; set; } = ChangesetOwnership.Legacy;

    /// <summary>Absolute path of the mod's staging tree, when the install was staged.</summary>
    public string? StagingPath { get; set; }

    /// <summary>
    /// Staged files as <c>&lt;root&gt;/&lt;root-relative path&gt;</c>.
    /// </summary>
    public List<string> StagedFiles { get; set; } = new();

    /// <summary>Layout findings reported by the game module after staging.</summary>
    public List<ModDiagnostic> Diagnostics { get; set; } = new();

    /// <summary>Whether the mod was deployed into the game folder.</summary>
    public bool Deployed { get; set; }

    /// <summary>The deployment that ran, when one did.</summary>
    public Deployment.DeploymentResult? Deployment { get; set; }

    /// <summary>Non-fatal findings worth showing the user.</summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>The choices the archive offered, each marked with what was installed.</summary>
    public List<InstallChoice> Choices { get; set; } = new();
    public List<string> InstalledFiles { get; set; } = new();
    public List<string> BackedUpFiles { get; set; } = new();
    public List<string> PlannedOperations { get; set; } = new();
    public string? Error { get; set; }
}

/// <summary>
/// Result of a mod uninstallation operation.
/// </summary>
public class ModUninstallResult
{
    /// <summary>Whether the mod's staging tree was removed.</summary>
    public bool StagingRemoved { get; set; }

    /// <summary>The redeployment that removed the mod, when the mod was managed.</summary>
    public Deployment.DeploymentResult? Deployment { get; set; }

    public bool Success { get; set; }
    public string ChangesetId { get; set; } = string.Empty;
    public int FilesRemoved { get; set; }
    public int FilesRestored { get; set; }
    public string? Error { get; set; }
}
