using System.Runtime.InteropServices;

namespace Modular.Core.GameDetection;

/// <summary>
/// Interface for locating the Steam installation root.
/// </summary>
public interface ISteamLocator
{
    /// <summary>
    /// Attempts to find the Steam installation root directory.
    /// </summary>
    /// <returns>The Steam root path, or null if not found.</returns>
    string? FindSteamRoot();
}

/// <summary>
/// Platform-specific Steam root discovery.
/// Linux: ~/.local/share/Steam or ~/.steam/steam
/// macOS: ~/Library/Application Support/Steam
/// Windows: Registry HKCU\Software\Valve\Steam\SteamPath
/// </summary>
public class SteamLocator : ISteamLocator
{
    /// <inheritdoc />
    public string? FindSteamRoot()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return FindLinuxSteamRoot();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return FindMacOSSteamRoot();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return FindWindowsSteamRoot();

        return null;
    }

    private static string? FindLinuxSteamRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Standard paths in order of preference
        string[] candidates =
        [
            Path.Combine(home, ".local", "share", "Steam"),
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".steam", "debian-installation"),
            // Flatpak
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
            // Snap
            Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam")
        ];

        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static string? FindMacOSSteamRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var steamPath = Path.Combine(home, "Library", "Application Support", "Steam");
        return Directory.Exists(steamPath) ? steamPath : null;
    }

    private static string? FindWindowsSteamRoot()
    {
        // Try common Windows paths
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var steamPath = Path.Combine(programFiles, "Steam");
        if (Directory.Exists(steamPath))
            return steamPath;

        // Try Program Files (non-x86)
        programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        steamPath = Path.Combine(programFiles, "Steam");
        return Directory.Exists(steamPath) ? steamPath : null;
    }
}
