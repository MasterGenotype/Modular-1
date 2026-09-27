using Modular.Sdk.Installers;
using Modular.Sdk.Modules;

namespace Modular.Core.Modules;

/// <summary>
/// Fallback module used when no specific module claims a directory.
/// </summary>
/// <remarks>
/// The game root is the install directory, only universal installers apply, and
/// deploy paths are mapped through unchanged. Games with no dedicated module — for
/// example Horizon Zero Dawn Remastered, whose mods are root-level DLL loaders —
/// land here and are handled by the loose-file installer.
/// </remarks>
public sealed class GenericGameModule : IGameModule
{
    /// <summary>The module id reported for any unclaimed game.</summary>
    public const string Id = "generic";

    /// <inheritdoc />
    public string ModuleId => Id;

    /// <inheritdoc />
    public string DisplayName => "Generic Game";

    /// <inheritdoc />
    public IReadOnlyList<string> GameIds { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<int> SteamAppIds { get; } = Array.Empty<int>();

    /// <summary>
    /// Claims any directory that exists, at a confidence low enough that every
    /// specific module outranks it.
    /// </summary>
    /// <param name="installPath">Candidate install directory.</param>
    /// <returns>A low-confidence match, or <c>null</c> when the path is not a directory.</returns>
    public GameModuleMatch? Detect(string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
            return null;

        return new GameModuleMatch
        {
            Confidence = 0.05,
            Evidence = ["fallback: no specific module claimed this directory"],
            ResolvedInstallPath = installPath
        };
    }

    /// <inheritdoc />
    public GameModPaths ResolveModPaths(GameInstallation installation) =>
        GameModPaths.ForInstallation(installation);

    /// <summary>
    /// Contributes no installers of its own; only the universal installers apply.
    /// </summary>
    /// <returns>An empty list.</returns>
    public IReadOnlyList<IModInstaller> GetInstallers() => Array.Empty<IModInstaller>();
}
