using System.Runtime.CompilerServices;
using Modular.Core.Utilities;

namespace Modular.Core.GameDetection;

/// <summary>
/// Scans Steam library roots for installed games by parsing appmanifest_*.acf files.
/// </summary>
public class SteamGameScanner
{
    private readonly SteamLibraryScanner _libraryScanner;

    public SteamGameScanner(SteamLibraryScanner? libraryScanner = null)
    {
        _libraryScanner = libraryScanner ?? new SteamLibraryScanner();
    }

    /// <summary>
    /// Streams all detected Steam game installations across all library roots.
    /// </summary>
    public async IAsyncEnumerable<SteamGameInstall> ScanAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var roots = _libraryScanner.GetLibraryRoots();

        foreach (var root in roots)
        {
            var steamApps = Path.Combine(root, "steamapps");
            if (!Directory.Exists(steamApps))
                continue;

            var manifests = Directory.GetFiles(steamApps, "appmanifest_*.acf");
            foreach (var manifest in manifests)
            {
                ct.ThrowIfCancellationRequested();

                SteamGameInstall? game = null;
                try
                {
                    game = ParseManifest(manifest, root);
                }
                catch
                {
                    // Skip unparseable manifests
                }

                if (game != null)
                    yield return game;
            }
        }

        await Task.CompletedTask; // Make truly async if needed in the future
    }

    /// <summary>
    /// Scans and returns all games as a list.
    /// </summary>
    public async Task<List<SteamGameInstall>> ScanAllAsync(CancellationToken ct = default)
    {
        var results = new List<SteamGameInstall>();
        await foreach (var game in ScanAsync(ct))
        {
            results.Add(game);
        }
        return results;
    }

    private static SteamGameInstall? ParseManifest(string manifestPath, string libraryRoot)
    {
        var kv = KeyValuesParser.ParseFile(manifestPath);
        var appState = kv.GetChild("AppState");

        if (appState == null)
            return null;

        var appIdStr = appState.GetValue("appid");
        if (!int.TryParse(appIdStr, out var appId))
            return null;

        var installDir = appState.GetValue("installdir");
        if (string.IsNullOrEmpty(installDir))
            return null;

        var name = appState.GetValue("name") ?? $"App {appId}";
        var sizeStr = appState.GetValue("SizeOnDisk");
        long.TryParse(sizeStr, out var sizeOnDisk);

        var stateFlagsStr = appState.GetValue("StateFlags");
        int.TryParse(stateFlagsStr, out var stateFlags);

        var installPath = Path.Combine(libraryRoot, "steamapps", "common", installDir);

        return new SteamGameInstall
        {
            AppId = appId,
            DisplayName = name,
            InstallDirectory = installDir,
            InstallPath = installPath,
            LibraryRoot = libraryRoot,
            SizeOnDisk = sizeOnDisk,
            StateFlags = stateFlags,
            IsFullyInstalled = stateFlags == 4, // StateFlags 4 = fully installed
            ManifestPath = manifestPath
        };
    }
}

/// <summary>
/// Represents a detected Steam game installation.
/// </summary>
public class SteamGameInstall
{
    /// <summary>
    /// Steam Application ID.
    /// </summary>
    public int AppId { get; set; }

    /// <summary>
    /// Display name of the game.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// The install directory name (from ACF).
    /// </summary>
    public string InstallDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Full install path: {library}/steamapps/common/{installdir}
    /// </summary>
    public string InstallPath { get; set; } = string.Empty;

    /// <summary>
    /// Library root containing this game.
    /// </summary>
    public string LibraryRoot { get; set; } = string.Empty;

    /// <summary>
    /// Size on disk in bytes.
    /// </summary>
    public long SizeOnDisk { get; set; }

    /// <summary>
    /// Steam state flags (4 = fully installed).
    /// </summary>
    public int StateFlags { get; set; }

    /// <summary>
    /// Whether the game is fully installed.
    /// </summary>
    public bool IsFullyInstalled { get; set; }

    /// <summary>
    /// Path to the ACF manifest file.
    /// </summary>
    public string ManifestPath { get; set; } = string.Empty;

    public override string ToString() => $"{DisplayName} (AppID: {AppId})";
}
