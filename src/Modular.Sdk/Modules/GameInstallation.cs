namespace Modular.Sdk.Modules;

/// <summary>
/// The storefront a game installation came from.
/// </summary>
public enum GameStore
{
    /// <summary>Store could not be determined.</summary>
    Unknown,

    /// <summary>Valve Steam.</summary>
    Steam,

    /// <summary>GOG Galaxy / GOG offline installer.</summary>
    Gog,

    /// <summary>Epic Games Store.</summary>
    Epic,

    /// <summary>Xbox / Microsoft Store.</summary>
    Xbox,

    /// <summary>A manually supplied directory.</summary>
    Manual
}

/// <summary>
/// A fully resolved game installation: where the game lives, which store it came
/// from, which <see cref="IGameModule"/> claimed it, and (on Linux) which Proton
/// prefix backs it.
/// </summary>
/// <remarks>
/// Produced by the core game-installation resolver and passed to modules so they
/// never need to know about Steam libraries or Wine prefixes themselves.
/// </remarks>
public sealed class GameInstallation
{
    /// <summary>
    /// Canonical game identifier. Normally a NexusMods slug such as
    /// <c>cyberpunk2077</c>.
    /// </summary>
    public string GameId { get; init; } = string.Empty;

    /// <summary>
    /// Steam Application ID, when the installation was resolved through Steam.
    /// </summary>
    public int? SteamAppId { get; init; }

    /// <summary>
    /// Absolute path to the game's install root (the directory a module anchors on).
    /// </summary>
    public string InstallPath { get; init; } = string.Empty;

    /// <summary>
    /// Store this installation came from.
    /// </summary>
    public GameStore Store { get; init; } = GameStore.Unknown;

    /// <summary>
    /// Absolute path to the Proton/Wine prefix backing this installation, or
    /// <c>null</c> when the game runs natively.
    /// </summary>
    /// <remarks>
    /// For Steam this is <c>&lt;library&gt;/steamapps/compatdata/&lt;appid&gt;/pfx</c>.
    /// </remarks>
    public string? ProtonPrefixPath { get; init; }

    /// <summary>
    /// Engine family reported by engine detection (for example <c>Unreal</c>).
    /// </summary>
    public string? EngineFamily { get; init; }

    /// <summary>
    /// Confidence of the <see cref="EngineFamily"/> detection (0.0 - 1.0).
    /// </summary>
    public double? EngineConfidence { get; init; }

    /// <summary>
    /// Identifier of the <see cref="IGameModule"/> that claimed this installation.
    /// </summary>
    public string? ModuleId { get; init; }

    /// <summary>
    /// Store build identifier, used to detect that the game was updated and that
    /// previously captured vanilla backups are therefore stale.
    /// </summary>
    /// <remarks>
    /// For Steam this is the <c>buildid</c> field of <c>appmanifest_&lt;appid&gt;.acf</c>.
    /// </remarks>
    public string? BuildId { get; init; }

    /// <summary>
    /// Display name of the game, for user-facing output.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Steam library root containing this installation, when applicable.
    /// </summary>
    public string? LibraryRoot { get; init; }

    /// <summary>
    /// A stable key identifying this installation in the database and on disk
    /// (staging folders, deployment manifests, backups).
    /// </summary>
    /// <remarks>
    /// Prefers the Steam AppID because it is stable across reinstalls and renames;
    /// falls back to the game id.
    /// </remarks>
    public string GameKey => SteamAppId is { } appId
        ? appId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : (string.IsNullOrEmpty(GameId) ? "unknown" : GameId);

    /// <summary>Whether this installation runs through a Proton/Wine prefix.</summary>
    public bool IsProton => !string.IsNullOrEmpty(ProtonPrefixPath);

    /// <inheritdoc />
    public override string ToString() =>
        $"{DisplayName ?? GameId} [{GameKey}] at {InstallPath}";
}

/// <summary>
/// Names of the deployment roots the SDK defines for every game.
/// Modules may add their own named roots on top of these.
/// </summary>
/// <remarks>
/// A "root" is a base directory that staged files can be deployed into. Mods do
/// not all belong in the game folder: user configuration frequently lives under
/// Documents, and several games keep their mod folder under LocalAppData.
/// </remarks>
public static class ModRoots
{
    /// <summary>The game's install directory. The default root.</summary>
    public const string Game = "game";

    /// <summary>
    /// <c>%LOCALAPPDATA%</c> on Windows;
    /// <c>&lt;pfx&gt;/drive_c/users/steamuser/AppData/Local</c> under Proton.
    /// </summary>
    public const string LocalAppData = "localappdata";

    /// <summary>
    /// The user's Documents folder on Windows;
    /// <c>&lt;pfx&gt;/drive_c/users/steamuser/Documents</c> under Proton.
    /// </summary>
    public const string Documents = "documents";

    /// <summary>
    /// Token form of <see cref="Game"/>, for use in declarative route tables.
    /// </summary>
    public const string GameToken = "$GAME";

    /// <summary>Token form of <see cref="LocalAppData"/>.</summary>
    public const string LocalAppDataToken = "$LOCALAPPDATA";

    /// <summary>Token form of <see cref="Documents"/>.</summary>
    public const string DocumentsToken = "$DOCUMENTS";

    /// <summary>
    /// Turns a route table's root token into a root name.
    /// </summary>
    /// <param name="rootOrToken">
    /// A root name, or a <c>$TOKEN</c> form. A module-defined token such as
    /// <c>$USERCONFIG</c> resolves to its lowercase name.
    /// </param>
    /// <returns>The root name, or the input unchanged when it is not a token.</returns>
    /// <remarks>
    /// Tokens exist so a route table never contains a Steam-library or Wine-prefix path: a
    /// module says <c>$DOCUMENTS</c> and the resolver decides whether that means the user's
    /// real Documents folder or one inside a Proton prefix.
    /// </remarks>
    public static string ResolveToken(string rootOrToken)
    {
        if (string.IsNullOrEmpty(rootOrToken) || rootOrToken[0] != '$')
            return rootOrToken;

        return rootOrToken.ToUpperInvariant() switch
        {
            GameToken => Game,
            LocalAppDataToken => LocalAppData,
            DocumentsToken => Documents,
            // A module-defined root: "$USERCONFIG" is the "userconfig" root.
            _ => rootOrToken[1..].ToLowerInvariant()
        };
    }

    /// <summary>
    /// Resolves the absolute path of one of the standard roots for an installation.
    /// </summary>
    /// <param name="installation">The resolved game installation.</param>
    /// <param name="rootName">One of <see cref="Game"/>, <see cref="LocalAppData"/> or <see cref="Documents"/>.</param>
    /// <returns>The absolute path, or <c>null</c> when the root name is not a standard root.</returns>
    public static string? ResolveStandardRoot(GameInstallation installation, string rootName)
    {
        ArgumentNullException.ThrowIfNull(installation);

        if (string.Equals(rootName, Game, StringComparison.OrdinalIgnoreCase))
            return installation.InstallPath;

        var prefix = installation.ProtonPrefixPath;

        if (string.Equals(rootName, LocalAppData, StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrEmpty(prefix)
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : Path.Combine(prefix, "drive_c", "users", "steamuser", "AppData", "Local");
        }

        if (string.Equals(rootName, Documents, StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrEmpty(prefix)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : Path.Combine(prefix, "drive_c", "users", "steamuser", "Documents");
        }

        return null;
    }
}

/// <summary>
/// The set of named deployment roots for one game installation.
/// </summary>
/// <remarks>
/// Built by <see cref="IGameModule.ResolveModPaths"/>. Every module gets the three
/// standard roots for free and may add game-specific ones (for example FF7 Remake's
/// <c>userconfig</c> root, which points inside Documents).
/// </remarks>
public sealed class GameModPaths
{
    private readonly Dictionary<string, string> _roots;

    /// <summary>
    /// Creates a set of named roots.
    /// </summary>
    /// <param name="roots">Root name to absolute path.</param>
    /// <param name="defaultRoot">Name of the root used when an operation does not name one.</param>
    public GameModPaths(IReadOnlyDictionary<string, string> roots, string defaultRoot = ModRoots.Game)
    {
        ArgumentNullException.ThrowIfNull(roots);

        _roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, path) in roots)
            _roots[name] = path;

        DefaultRoot = defaultRoot;
    }

    /// <summary>
    /// Name of the root used for operations that do not carry an explicit root.
    /// </summary>
    public string DefaultRoot { get; }

    /// <summary>
    /// All named roots, keyed case-insensitively by root name.
    /// </summary>
    public IReadOnlyDictionary<string, string> Roots => _roots;

    /// <summary>
    /// Gets the absolute path of a named root.
    /// </summary>
    /// <param name="rootName">
    /// Root name or <c>$TOKEN</c>; <c>null</c> or empty means <see cref="DefaultRoot"/>.
    /// </param>
    /// <returns>The absolute path, or <c>null</c> when no such root is defined.</returns>
    public string? GetRoot(string? rootName)
    {
        var name = string.IsNullOrEmpty(rootName)
            ? DefaultRoot
            : ModRoots.ResolveToken(rootName);

        return _roots.TryGetValue(name, out var path) ? path : null;
    }

    /// <summary>
    /// Combines a named root with a root-relative path.
    /// </summary>
    /// <param name="rootName">Root name; <c>null</c> or empty means <see cref="DefaultRoot"/>.</param>
    /// <param name="relativePath">Path relative to that root.</param>
    /// <returns>The absolute destination path.</returns>
    /// <exception cref="ArgumentException">The named root is not defined.</exception>
    public string Resolve(string? rootName, string relativePath)
    {
        var root = GetRoot(rootName)
            ?? throw new ArgumentException(
                $"Unknown deployment root '{rootName}'.", nameof(rootName));

        return Path.Combine(root, relativePath.Replace('\\', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// Builds a <see cref="GameModPaths"/> containing the three standard roots,
    /// plus any extra roots supplied by a module.
    /// </summary>
    /// <param name="installation">The resolved game installation.</param>
    /// <param name="extraRoots">Additional module-specific roots, or <c>null</c>.</param>
    /// <returns>The resolved root set.</returns>
    public static GameModPaths ForInstallation(
        GameInstallation installation,
        IReadOnlyDictionary<string, string>? extraRoots = null)
    {
        ArgumentNullException.ThrowIfNull(installation);

        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var standard in new[] { ModRoots.Game, ModRoots.LocalAppData, ModRoots.Documents })
        {
            var path = ModRoots.ResolveStandardRoot(installation, standard);
            if (!string.IsNullOrEmpty(path))
                roots[standard] = path;
        }

        if (extraRoots != null)
        {
            foreach (var (name, path) in extraRoots)
                roots[name] = path;
        }

        return new GameModPaths(roots);
    }
}
