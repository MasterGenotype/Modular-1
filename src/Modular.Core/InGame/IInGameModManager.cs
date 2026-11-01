using Modular.Sdk.Modules;

namespace Modular.Core.InGame;

/// <summary>
/// A game's own mod manager, which downloads the mods the user subscribes to on a mod hub
/// and owns their files: Baldur's Gate 3's, fed by mod.io.
/// </summary>
/// <remarks>
/// Modular never places a file such a manager would download itself. It subscribes, lets
/// the game download, and then keeps track of what the game put there, so the game never
/// finds a copy it does not recognise and downloads the mod a second time.
/// </remarks>
public interface IInGameModManager
{
    /// <summary>The hub the game's manager downloads from, such as <c>modio</c>.</summary>
    string BackendId { get; }

    /// <summary>The game's id on that hub, such as <c>6715</c>.</summary>
    string RemoteGameId { get; }

    /// <summary>Whether the game is running, in which case its files must not be touched.</summary>
    bool IsGameRunning();

    /// <summary>
    /// The name the game gives the file it makes from a hub upload.
    /// </summary>
    /// <param name="uploadFileName">The upload's file name on the hub.</param>
    /// <returns>The file name in the game's mods folder.</returns>
    string FileNameFor(string uploadFileName);

    /// <summary>Lists the mods in the game's mods folder, whoever put them there.</summary>
    /// <param name="installation">The game.</param>
    /// <returns>Every readable mod file.</returns>
    IReadOnlyList<InGameModFile> Scan(GameInstallation installation);

    /// <summary>
    /// Removes a game-owned mod: its file and its load-order entry.
    /// </summary>
    /// <param name="installation">The game.</param>
    /// <param name="file">The mod's file, as <see cref="Scan"/> found it.</param>
    /// <returns>What was removed, for reporting.</returns>
    /// <exception cref="InvalidOperationException">The game is running.</exception>
    IReadOnlyList<string> Remove(GameInstallation installation, InGameModFile file);
}

/// <summary>
/// A mod file in a game's mods folder.
/// </summary>
/// <param name="FileName">The file's name.</param>
/// <param name="FullPath">The file's absolute path.</param>
/// <param name="ModuleUuid">The module it contains, lowercase.</param>
/// <param name="Name">The module's name.</param>
/// <param name="RemoteModId">The hub's id for the mod, from the file's own metadata, or null.</param>
/// <param name="Version64">The module's version, packed as the game packs it.</param>
/// <param name="UploadMd5">The upload hash the metadata records, or empty.</param>
/// <param name="HasModularName">Whether the name is one Modular gives the files it deploys.</param>
public sealed record InGameModFile(
    string FileName,
    string FullPath,
    string ModuleUuid,
    string Name,
    string? RemoteModId,
    long Version64,
    string UploadMd5,
    bool HasModularName);

/// <summary>The games whose own mod manager Modular works alongside.</summary>
public static class InGameModManagers
{
    /// <summary>Baldur's Gate 3's Steam app id, which is also its Modular game key.</summary>
    public const int BaldursGate3SteamAppId = 1086940;

    /// <summary>
    /// The game's own mod manager, or null for a game without one.
    /// </summary>
    /// <param name="installation">The game.</param>
    /// <returns>The adapter, or null.</returns>
    public static IInGameModManager? For(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);

        return installation.SteamAppId == BaldursGate3SteamAppId ||
               string.Equals(installation.ModuleId, "baldursgate3", StringComparison.OrdinalIgnoreCase)
            ? new BG3InGameModManager()
            : null;
    }
}
