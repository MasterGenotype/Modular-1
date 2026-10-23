using Modular.Sdk.Installers;

namespace Modular.Core.Installers;

/// <summary>
/// An installer whose detection depends on which game the archive is installed into.
/// </summary>
/// <remarks>
/// <see cref="IModInstaller.DetectAsync"/> sees only the archive, so a game-specific
/// installer must refuse layouts that could belong to any game, such as a bare
/// <c>.archive</c> or a folder with an <c>init.lua</c>. When the game is known,
/// <see cref="InstallerManager"/> calls this overload instead and the installer may
/// claim them.
/// </remarks>
public interface IGameAwareInstaller
{
    /// <summary>
    /// Detects if this installer can handle the given archive for the given game.
    /// </summary>
    /// <param name="archivePath">Archive to analyse.</param>
    /// <param name="gameId">Game identifier (slug or AppID), or <c>null</c> when unknown.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The detection result.</returns>
    Task<InstallDetectionResult> DetectAsync(string archivePath, string? gameId, CancellationToken ct = default);
}
