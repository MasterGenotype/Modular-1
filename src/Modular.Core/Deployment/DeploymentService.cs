using Microsoft.Extensions.Logging;
using Modular.Core.Configuration;
using Modular.Core.Database;
using Modular.Core.GameDetection;
using Modular.Core.Installers;
using Modular.Sdk.Modules;

namespace Modular.Core.Deployment;

/// <summary>
/// The entry point for deploying a game's mod set: resolve the game, read the ordered mod
/// list, plan, and apply.
/// </summary>
/// <remarks>
/// Everything that changes a game folder goes through here, so "install", "reorder",
/// "uninstall" and "restore a snapshot" all end in the same single deploy. That is what
/// makes a restore of N mods one deployment instead of N purge-and-redeploy cycles.
/// </remarks>
public sealed class DeploymentService
{
    private readonly GameModRepository _mods;
    private readonly StagingLayout _layout;
    private readonly DeploymentPlanner _planner;
    private readonly IDeployer _deployer;
    private readonly GameInstallationResolver _resolver;
    private readonly AppSettings? _settings;
    private readonly ILogger<DeploymentService>? _logger;

    /// <summary>
    /// Creates the service.
    /// </summary>
    /// <param name="database">Modular database.</param>
    /// <param name="staging">Staging manager owning the per-mod trees.</param>
    /// <param name="resolver">
    /// Game installation resolver. It carries the module registry it was built with, which is
    /// how this service finds the module owning a game.
    /// </param>
    /// <param name="settings">App settings, for the configured deployment method.</param>
    /// <param name="deployer">
    /// Deployer; a default one is created when omitted. Injectable so a caller can count
    /// deployments.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public DeploymentService(
        ModularDatabase database,
        StagingManager staging,
        GameInstallationResolver resolver,
        AppSettings? settings = null,
        IDeployer? deployer = null,
        ILogger<DeploymentService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(staging);

        _mods = new GameModRepository(database);
        _layout = new StagingLayout(staging.BasePath);
        _planner = new DeploymentPlanner();
        _deployer = deployer ?? new Deployer(_layout);
        _resolver = resolver;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>The repository of deployed mods, for callers that only need the list.</summary>
    public GameModRepository Mods => _mods;

    /// <summary>
    /// Deploys the enabled mods of a game, in order.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <param name="methodOverride">Method for this run only, from <c>--method</c>.</param>
    /// <param name="loadOrderOverride">
    /// An explicit load order, normally a profile's. Mods it does not name keep their
    /// stored relative order afterwards.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="force">
    /// Lay every file down again from staging, even those already in place.
    /// </param>
    /// <returns>What the deployment did.</returns>
    public async Task<DeploymentResult> DeployAsync(
        GameInstallation installation,
        DeploymentMethod? methodOverride = null,
        IReadOnlyList<string>? loadOrderOverride = null,
        CancellationToken ct = default,
        bool force = false)
    {
        ArgumentNullException.ThrowIfNull(installation);

        var module = _resolver.GetModule(installation);
        var modPaths = module.ResolveModPaths(installation);
        var settings = await ResolveSettingsAsync(installation.GameKey, methodOverride, ct) with { Force = force };

        var records = await _mods.ListAsync(installation.GameKey, enabledOnly: true, ct);
        var ordered = ApplyLoadOrderOverride(records, loadOrderOverride);

        var inputs = new List<DeploymentModInput>();
        var missing = new List<string>();

        for (var i = 0; i < ordered.Count; i++)
        {
            var record = ordered[i];

            if (!Directory.Exists(record.StagingPath))
            {
                missing.Add(record.ModId);
                continue;
            }

            inputs.Add(new DeploymentModInput(
                record.ModId,
                record.StagingPath,
                i,
                StagingManager.EnumerateStagedFiles(record.StagingPath)));
        }

        var current = DeploymentManifest.Load(
            _layout.GetGameStagingDirectory(installation.GameKey), installation.GameKey);

        var plan = _planner.Plan(installation, module, modPaths, inputs, current);

        DeploymentResult result;
        try
        {
            result = await _deployer.DeployAsync(installation, module, plan, settings, ct);
        }
        catch (DeploymentAbortedException ex)
        {
            return new DeploymentResult
            {
                GameKey = installation.GameKey,
                RequestedMethod = settings.Method,
                MethodSource = settings.Source,
                Error = ex.Message
            };
        }

        foreach (var modId in missing)
        {
            result.Warnings.Add(
                $"Mod '{modId}' has no staged files left and was not deployed. " +
                "Reinstall it from its archive, or remove it.");
        }

        foreach (var overlap in plan.Overlaps)
        {
            _logger?.LogDebug(
                "{Winner} overrides {Loser} at {Root}/{Path}",
                overlap.WinnerModId, overlap.LoserModId, overlap.Root, overlap.RelativePath);
        }

        result.Plan = plan;
        return result;
    }

    /// <summary>
    /// Resolves a game reference and deploys it.
    /// </summary>
    /// <param name="gameRef">AppID, slug, display name or path.</param>
    /// <param name="methodOverride">Method for this run only.</param>
    /// <param name="loadOrderOverride">An explicit load order.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What the deployment did, or a failure when the game could not be resolved.</returns>
    public async Task<DeploymentResult> DeployAsync(
        string gameRef,
        DeploymentMethod? methodOverride = null,
        IReadOnlyList<string>? loadOrderOverride = null,
        CancellationToken ct = default)
    {
        var installation = await _resolver.ResolveAsync(gameRef, persist: true, ct);

        if (installation == null)
        {
            return new DeploymentResult
            {
                Error = $"Could not resolve game '{gameRef}'."
            };
        }

        return await DeployAsync(installation, methodOverride, loadOrderOverride, ct);
    }

    /// <summary>
    /// Resolves a game reference to an installation.
    /// </summary>
    /// <param name="gameRef">AppID, slug, GameBanana id, display name or path.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The installation, or <c>null</c> when nothing matched.</returns>
    public Task<GameInstallation?> ResolveInstallationAsync(
        string gameRef, CancellationToken ct = default) =>
        _resolver.ResolveAsync(gameRef, persist: true, ct);

    /// <summary>
    /// Records a staged mod as part of a game's deployment.
    /// </summary>
    /// <param name="record">The mod to record.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored record, with its resolved order index.</returns>
    public Task<GameModRecord> RegisterModAsync(
        GameModRecord record, CancellationToken ct = default) =>
        _mods.UpsertAsync(record, ct);

    /// <summary>
    /// Removes a mod from a game and redeploys, then deletes its staging tree.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <param name="modId">Mod to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The redeployment result.</returns>
    /// <remarks>
    /// The order matters: staging is deleted only after the redeploy succeeds. A failed
    /// deploy therefore leaves a recoverable state rather than a game folder referencing
    /// bytes that no longer exist.
    /// </remarks>
    public async Task<DeploymentResult> RemoveModAsync(
        GameInstallation installation,
        string modId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(installation);

        var record = await _mods.GetAsync(installation.GameKey, modId, ct);

        // Read before the redeploy rewrites it: the manifest is the only record of which
        // deployed files were this mod's.
        var modFiles = DeploymentManifest
            .Load(_layout.GetGameStagingDirectory(installation.GameKey), installation.GameKey)
            .Files
            .Where(f => string.Equals(f.ModId, modId, StringComparison.OrdinalIgnoreCase))
            .Select(f => $"{f.Root}/{f.Path}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        await _mods.RemoveAsync(installation.GameKey, modId, ct);

        var result = await DeployAsync(installation, ct: ct);

        result.RemovedForMod.AddRange(result.Removed.Where(modFiles.Contains));

        if (result.Success && record != null && Directory.Exists(record.StagingPath))
        {
            Directory.Delete(record.StagingPath, recursive: true);
            result.StagingRemoved.Add(modId);
        }
        else if (!result.Success && record != null)
        {
            // Put the mod back so the recorded state still matches what is on disk.
            await _mods.UpsertAsync(record, ct);
        }

        return result;
    }

    /// <summary>
    /// Enables or disables mods of a game and redeploys, so the game folder matches.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <param name="modIds">Mods to change, matched without regard to case.</param>
    /// <param name="enabled">Whether the mods should be deployed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The redeployment result, or a failure naming any mod the game does not have, in
    /// which case nothing is changed.
    /// </returns>
    /// <remarks>
    /// A disabled mod keeps its staging and its place in the order; only its files leave
    /// the game folder. If the redeploy fails, the flags are put back so the recorded state
    /// still matches what is on disk.
    /// </remarks>
    public async Task<DeploymentResult> SetEnabledAsync(
        GameInstallation installation,
        IReadOnlyCollection<string> modIds,
        bool enabled,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(modIds);

        var known = await _mods.ListAsync(installation.GameKey, enabledOnly: false, ct);
        var targets = new List<GameModRecord>();
        var unknown = new List<string>();

        foreach (var id in modIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var record = known.FirstOrDefault(m =>
                string.Equals(m.ModId, id, StringComparison.OrdinalIgnoreCase));

            if (record == null)
                unknown.Add(id);
            else
                targets.Add(record);
        }

        if (unknown.Count > 0)
        {
            return new DeploymentResult
            {
                GameKey = installation.GameKey,
                Error = $"Not installed for this game: {string.Join(", ", unknown)}"
            };
        }

        var changed = targets.Where(m => m.Enabled != enabled).ToList();

        foreach (var record in changed)
            await _mods.SetEnabledAsync(installation.GameKey, record.ModId, enabled, ct);

        var result = await DeployAsync(installation, ct: ct);

        if (!result.Success)
        {
            foreach (var record in changed)
                await _mods.SetEnabledAsync(installation.GameKey, record.ModId, record.Enabled, CancellationToken.None);
        }

        return result;
    }

    /// <summary>
    /// Resolves which deployment method a game uses.
    /// </summary>
    /// <param name="gameKey">Game key.</param>
    /// <param name="methodOverride">Method from a command flag, if any.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The settings, with their source named for the report.</returns>
    /// <remarks>
    /// Precedence is flag, then per-game setting, then <c>config.json</c>, then the
    /// hardlink default.
    /// </remarks>
    public async Task<DeploymentSettings> ResolveSettingsAsync(
        string gameKey,
        DeploymentMethod? methodOverride = null,
        CancellationToken ct = default)
    {
        var fallback = DeploymentMethods.ParseFallback(_settings?.DeploymentFallback)
                       ?? DeploymentFallback.Copy;

        if (methodOverride is { } flag)
            return new DeploymentSettings(flag, fallback, "--method");

        if (await _mods.GetDeploySettingsAsync(gameKey, ct) is { } stored)
            return stored with { Fallback = fallback };

        if (DeploymentMethods.ParseMethod(_settings?.DeploymentMethod) is { } configured)
            return new DeploymentSettings(configured, fallback, "config.json");

        return DeploymentSettings.Default with { Fallback = fallback };
    }

    /// <summary>
    /// Stores a game's deployment method, purging first when the method changes.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <param name="method">Method to store.</param>
    /// <param name="fallback">Fallback to store.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The deployment result of the fresh deploy, or <c>null</c> when nothing was deployed.</returns>
    /// <remarks>
    /// Identity records are not comparable across methods, so a change forces a full purge
    /// and a fresh deploy rather than an incremental one.
    /// </remarks>
    public async Task<DeploymentResult?> SetDeploymentMethodAsync(
        GameInstallation installation,
        DeploymentMethod method,
        DeploymentFallback fallback = DeploymentFallback.Copy,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(installation);

        var previous = await ResolveSettingsAsync(installation.GameKey, null, ct);
        await _mods.SetDeploySettingsAsync(installation.GameKey, method, fallback, ct);

        if (previous.Method == method)
            return null;

        _logger?.LogInformation(
            "Deployment method for {GameKey} changed {Old} → {New}; purging first",
            installation.GameKey, previous.Method.ToName(), method.ToName());

        _deployer.Purge(installation);

        return await DeployAsync(installation, method, ct: ct);
    }

    /// <summary>
    /// Reports which methods the pre-flight probe says will work for each root.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <returns>Per root name, the usable methods.</returns>
    public Dictionary<string, List<DeploymentMethod>> ProbeMethods(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);

        var module = _resolver.GetModule(installation);
        var modPaths = module.ResolveModPaths(installation);

        var plan = new DeploymentPlan();
        foreach (var (name, path) in modPaths.Roots)
            plan.Roots[name] = path;

        return _deployer.ProbeAll(plan);
    }

    /// <summary>
    /// Removes everything Modular has deployed for a game.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <returns>What was removed and restored.</returns>
    public DeploymentResult Purge(GameInstallation installation) =>
        _deployer.Purge(installation);

    /// <summary>
    /// Applies an explicit load order on top of the stored one.
    /// </summary>
    /// <param name="records">Stored mods, already in stored order.</param>
    /// <param name="loadOrder">The explicit order, or <c>null</c> to keep the stored one.</param>
    /// <returns>The mods in effective order.</returns>
    /// <remarks>
    /// A profile naming only some mods is a partial instruction, not a complete one: the
    /// mods it names come first in its order, and the rest follow in their stored order.
    /// </remarks>
    private static List<GameModRecord> ApplyLoadOrderOverride(
        List<GameModRecord> records, IReadOnlyList<string>? loadOrder)
    {
        if (loadOrder == null || loadOrder.Count == 0)
            return records;

        var byId = records.ToDictionary(r => r.ModId, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<GameModRecord>();

        foreach (var modId in loadOrder)
        {
            if (byId.Remove(modId, out var record))
                ordered.Add(record);
        }

        ordered.AddRange(records.Where(r => byId.ContainsKey(r.ModId)));
        return ordered;
    }
}
