using Modular.Core.Utilities;

namespace Modular.Core.GameDetection;

/// <summary>
/// Scans Steam library folders by parsing libraryfolders.vdf.
/// </summary>
public class SteamLibraryScanner
{
    private readonly ISteamLocator _locator;

    public SteamLibraryScanner(ISteamLocator? locator = null)
    {
        _locator = locator ?? new SteamLocator();
    }

    /// <summary>
    /// Discovers all Steam library root directories (each contains a steamapps/ folder).
    /// </summary>
    /// <returns>List of library root paths.</returns>
    public List<string> GetLibraryRoots()
    {
        var roots = new List<string>();
        var steamRoot = _locator.FindSteamRoot();

        if (string.IsNullOrEmpty(steamRoot))
            return roots;

        // The Steam root itself is always a library
        var mainSteamApps = Path.Combine(steamRoot, "steamapps");
        if (Directory.Exists(mainSteamApps))
            roots.Add(steamRoot);

        // Parse libraryfolders.vdf for additional libraries
        var vdfPath = Path.Combine(mainSteamApps, "libraryfolders.vdf");
        if (!File.Exists(vdfPath))
            return roots;

        try
        {
            var kv = KeyValuesParser.ParseFile(vdfPath);
            var libraryFolders = kv.GetChild("libraryfolders");

            if (libraryFolders == null)
                return roots;

            // Each numbered child (0, 1, 2, ...) represents a library folder
            foreach (var child in libraryFolders.Children)
            {
                var path = child.GetValue("path");
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    if (!roots.Contains(path, StringComparer.OrdinalIgnoreCase))
                        roots.Add(path);
                }
            }
        }
        catch
        {
            // If VDF parsing fails, fall back to just the main root
        }

        return roots;
    }
}
