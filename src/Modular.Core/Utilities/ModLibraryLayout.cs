using Modular.Sdk.Backends;

namespace Modular.Core.Utilities;

/// <summary>
/// A game's folder in the mods directory.
/// </summary>
/// <param name="BackendId">The backend the mods in it were downloaded from.</param>
/// <param name="Game">The game folder's name.</param>
/// <param name="Path">Full path of the folder.</param>
public sealed record LibraryGameFolder(string BackendId, string Game, string Path);

/// <summary>
/// Where downloads go in the mods directory: <c>&lt;root&gt;/&lt;backend&gt;/&lt;game&gt;/&lt;mod&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// The backend folder is the backend's id (<c>nexusmods</c>, <c>gamebanana</c>,
/// <c>modio</c>). The game folder is the NexusMods game domain for NexusMods, and the
/// game's name with everything but letters and digits removed for the others, so
/// Baldur's Gate 3 is <c>baldursgate3</c> whichever site a mod came from.
/// </para>
/// <para>
/// Dot-folders in the root, such as the archive store, are Modular's own and are never
/// games or backends.
/// </para>
/// </remarks>
public static class ModLibraryLayout
{
    /// <summary>Game folder used when a download's game is not known.</summary>
    public const string UnknownGame = "unknown";

    /// <summary>The backends that have a folder, in display order.</summary>
    public static IReadOnlyList<string> Backends { get; } =
        [BackendIds.NexusMods, BackendIds.GameBanana, BackendIds.ModIo];

    /// <summary>Whether a root-level folder name is a backend folder.</summary>
    /// <param name="name">Folder name.</param>
    public static bool IsBackendFolder(string? name) =>
        name != null && Backends.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The folder holding everything downloaded from a backend.</summary>
    /// <param name="root">The mods directory.</param>
    /// <param name="backendId">The backend's id.</param>
    public static string BackendFolder(string root, string backendId)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(backendId);

        return Path.Combine(root, backendId.ToLowerInvariant());
    }

    /// <summary>The folder holding one game's downloads from a backend.</summary>
    /// <param name="root">The mods directory.</param>
    /// <param name="backendId">The backend's id.</param>
    /// <param name="game">
    /// The game: a NexusMods domain, or for other backends the game's name.
    /// </param>
    public static string GameFolder(string root, string backendId, string? game) =>
        Path.Combine(BackendFolder(root, backendId), GameFolderName(backendId, game));

    /// <summary>The folder one mod's downloads go in.</summary>
    /// <param name="root">The mods directory.</param>
    /// <param name="backendId">The backend's id.</param>
    /// <param name="game">The game, as for <see cref="GameFolder"/>.</param>
    /// <param name="mod">The mod's folder name; sanitized here.</param>
    public static string ModFolder(string root, string backendId, string? game, string mod) =>
        Path.Combine(GameFolder(root, backendId, game), FileUtils.SanitizeDirectoryName(mod));

    /// <summary>
    /// The name of a game's folder.
    /// </summary>
    /// <param name="backendId">The backend's id.</param>
    /// <param name="game">A NexusMods domain, or another backend's name for the game.</param>
    /// <returns>The folder name; <see cref="UnknownGame"/> when there is nothing to go on.</returns>
    public static string GameFolderName(string backendId, string? game)
    {
        if (string.IsNullOrWhiteSpace(game))
            return UnknownGame;

        // A NexusMods domain is already a folder name, and is what its API calls the game.
        if (string.Equals(backendId, BackendIds.NexusMods, StringComparison.OrdinalIgnoreCase))
            return FileUtils.SanitizeDirectoryName(game.Trim());

        var slug = Slug(game);
        return slug.Length > 0 ? slug : FileUtils.SanitizeDirectoryName(game.Trim());
    }

    /// <summary>
    /// A game name with everything but letters and digits removed, lower case:
    /// "Baldur's Gate 3" is <c>baldursgate3</c>, as NexusMods spells it.
    /// </summary>
    /// <param name="name">The name.</param>
    public static string Slug(string name) =>
        new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>
    /// Lists every game folder, backend by backend.
    /// </summary>
    /// <param name="root">The mods directory.</param>
    /// <returns>Game folders, ordered by backend then name.</returns>
    public static IReadOnlyList<LibraryGameFolder> ListGames(string? root)
    {
        var games = new List<LibraryGameFolder>();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return games;

        foreach (var backend in Backends)
        {
            var backendFolder = BackendFolder(root, backend);
            if (!Directory.Exists(backendFolder))
                continue;

            foreach (var dir in Directory.GetDirectories(backendFolder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                if (!name.StartsWith('.') && !IsOldFlatModFolder(backend, dir))
                    games.Add(new LibraryGameFolder(backend, name, dir));
            }
        }

        return games;
    }

    /// <summary>
    /// Whether a folder in a backend folder is a mod folder from before game folders
    /// existed, rather than a game folder.
    /// </summary>
    /// <param name="backendId">The backend folder it is in.</param>
    /// <param name="folder">The folder.</param>
    /// <remarks>
    /// GameBanana and mod.io downloads used to go straight into <c>gamebanana/&lt;mod&gt;/</c>
    /// and <c>modio/&lt;mod&gt;/</c>. Such a folder holds the mod's archives itself, where a
    /// game folder holds only mod folders. NexusMods never had that layout, and its game
    /// folders may hold loose archives, so its folders are always games.
    /// </remarks>
    public static bool IsOldFlatModFolder(string backendId, string folder)
    {
        if (string.Equals(backendId, BackendIds.NexusMods, StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            return Directory.EnumerateFiles(folder).Any(f => !Path.GetFileName(f).StartsWith('.'));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Says which backend and game folder a path in the mods directory is under.
    /// </summary>
    /// <param name="root">The mods directory.</param>
    /// <param name="path">A file or folder path.</param>
    /// <returns>
    /// The backend and game folder name, or <c>null</c> when the path is not inside a game
    /// folder of the mods directory.
    /// </returns>
    public static (string BackendId, string Game)? Locate(string? root, string? path)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path))
            return null;

        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return null;

        var parts = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 3 || !IsBackendFolder(parts[0]))
            return null;

        return (parts[0].ToLowerInvariant(), parts[1]);
    }
}
