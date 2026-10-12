using Microsoft.Extensions.Logging;
using Modular.Core.Archives;
using Modular.Core.Metadata;
using Modular.Sdk.Archives;
using Modular.Sdk.Modules;

namespace Modular.Core.Modules;

/// <summary>
/// Holds the available <see cref="IGameModule"/>s and resolves which one owns a
/// given game id, Steam AppID or directory.
/// </summary>
/// <remarks>
/// Built-in modules are registered on construction. Plugin-supplied modules can be
/// added afterwards with <see cref="Register"/>. <see cref="GenericGameModule"/> is
/// always available as the fallback and is never returned by identifier lookup,
/// only by detection.
/// </remarks>
public sealed class GameModuleRegistry
{
    private readonly List<IGameModule> _modules = new();
    private readonly GenericGameModule _fallback = new();
    private readonly ILogger<GameModuleRegistry>? _logger;

    /// <summary>
    /// Creates a registry populated with the built-in modules.
    /// </summary>
    /// <param name="archiveReaderFactory">Shared archive reader factory handed to each module.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="categories">
    /// Mod hub categories, for modules that order mods by what kind of mod they are.
    /// </param>
    public GameModuleRegistry(
        IArchiveReaderFactory? archiveReaderFactory = null,
        ILogger<GameModuleRegistry>? logger = null,
        IModCategorySource? categories = null)
    {
        _logger = logger;

        var factory = archiveReaderFactory ?? new ArchiveReaderFactory();

        Register(new CyberpunkGameModule(factory));
        Register(new FF7RemakeGameModule(factory));
        Register(new HorizonZeroDawnGameModule(factory));
        Register(new BaldursGate3GameModule(factory, categories));
    }

    /// <summary>
    /// The fallback module used when nothing else claims a game.
    /// </summary>
    public IGameModule Fallback => _fallback;

    /// <summary>
    /// All registered modules, excluding the fallback.
    /// </summary>
    public IReadOnlyList<IGameModule> Modules => _modules.AsReadOnly();

    /// <summary>
    /// Registers a module, replacing any existing module with the same
    /// <see cref="IGameModule.ModuleId"/>.
    /// </summary>
    /// <param name="module">The module to register.</param>
    public void Register(IGameModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        var existing = _modules.FindIndex(m =>
            string.Equals(m.ModuleId, module.ModuleId, StringComparison.OrdinalIgnoreCase));

        if (existing >= 0)
        {
            _logger?.LogInformation(
                "Replacing game module {ModuleId} with {DisplayName}",
                module.ModuleId, module.DisplayName);
            _modules[existing] = module;
            return;
        }

        _modules.Add(module);
        _logger?.LogDebug(
            "Registered game module {ModuleId} ({DisplayName})",
            module.ModuleId, module.DisplayName);
    }

    /// <summary>
    /// Registers several modules.
    /// </summary>
    /// <param name="modules">The modules to register.</param>
    public void RegisterAll(IEnumerable<IGameModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        foreach (var module in modules)
            Register(module);
    }

    /// <summary>
    /// Finds the module with the given module id.
    /// </summary>
    /// <param name="moduleId">Module identifier.</param>
    /// <returns>The module, or <c>null</c> when it is not registered.</returns>
    public IGameModule? GetByModuleId(string? moduleId)
    {
        if (string.IsNullOrWhiteSpace(moduleId))
            return null;

        if (string.Equals(moduleId, GenericGameModule.Id, StringComparison.OrdinalIgnoreCase))
            return _fallback;

        return _modules.FirstOrDefault(m =>
            string.Equals(m.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finds the module claiming a game identifier: a NexusMods slug, a Steam
    /// AppID rendered as a string, or a GameBanana game id.
    /// </summary>
    /// <param name="gameId">The identifier to look up.</param>
    /// <returns>The module, or <c>null</c> when no module claims it.</returns>
    public IGameModule? ResolveByGameId(string? gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId))
            return null;

        var match = _modules.FirstOrDefault(m => m.GameIds.Any(id =>
            string.Equals(id, gameId, StringComparison.OrdinalIgnoreCase)));

        if (match != null)
            return match;

        // A numeric id that is not listed as a string may still match an AppID.
        return int.TryParse(gameId, out var appId) ? ResolveByAppId(appId) : null;
    }

    /// <summary>
    /// Finds the module claiming a Steam Application ID.
    /// </summary>
    /// <param name="appId">Steam AppID.</param>
    /// <returns>The module, or <c>null</c> when no module claims it.</returns>
    public IGameModule? ResolveByAppId(int appId) =>
        _modules.FirstOrDefault(m => m.SteamAppIds.Contains(appId));

    /// <summary>
    /// Runs every module's detection against a directory and returns the
    /// highest-confidence claim.
    /// </summary>
    /// <param name="installPath">Candidate install directory.</param>
    /// <returns>
    /// The winning module and its match. Falls back to
    /// <see cref="GenericGameModule"/> for any existing directory, so this only
    /// returns <c>null</c> when the path is not a directory at all.
    /// </returns>
    public GameModuleResolution? ResolveByPath(string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
            return null;

        GameModuleResolution? best = null;

        foreach (var module in _modules)
        {
            GameModuleMatch? match;
            try
            {
                match = module.Detect(installPath);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    ex, "Game module {ModuleId} threw during detection", module.ModuleId);
                continue;
            }

            if (match == null)
                continue;

            if (best == null || match.Confidence > best.Match.Confidence)
                best = new GameModuleResolution(module, match);
        }

        if (best != null)
        {
            _logger?.LogInformation(
                "Resolved {Path} to module {ModuleId} ({Confidence:P0})",
                installPath, best.Module.ModuleId, best.Match.Confidence);
            return best;
        }

        var fallbackMatch = _fallback.Detect(installPath);
        return fallbackMatch == null ? null : new GameModuleResolution(_fallback, fallbackMatch);
    }

    /// <summary>
    /// Resolves a module by identifier first, then by path detection, then falls
    /// back to the generic module.
    /// </summary>
    /// <param name="gameId">Game identifier, or <c>null</c> when unknown.</param>
    /// <param name="appId">Steam AppID, or <c>null</c> when unknown.</param>
    /// <param name="installPath">Install directory, or <c>null</c> when unknown.</param>
    /// <returns>The best module for the inputs; never <c>null</c>.</returns>
    public IGameModule Resolve(string? gameId, int? appId, string? installPath)
    {
        if (appId is { } id && ResolveByAppId(id) is { } byAppId)
            return byAppId;

        if (ResolveByGameId(gameId) is { } byGameId)
            return byGameId;

        if (!string.IsNullOrWhiteSpace(installPath) &&
            ResolveByPath(installPath) is { } byPath)
        {
            return byPath.Module;
        }

        return _fallback;
    }
}

/// <summary>
/// A module together with the evidence that made it win path detection.
/// </summary>
/// <param name="Module">The winning module.</param>
/// <param name="Match">Its confidence and evidence.</param>
public sealed record GameModuleResolution(IGameModule Module, GameModuleMatch Match);
