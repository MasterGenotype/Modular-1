namespace Modular.Switch.Totk.Helpers;

/// <summary>
/// Port of the path lookups in TKMM v2.0.0-rc1 <c>Tkmm.Core/Helpers/TkEmulatorHelper.cs</c>:
/// finding a Yuzu-family emulator's data folder from its executable and reading the
/// load, SD and NAND folders from its <c>qt-config.ini</c>. rc1's interactive setup
/// (<c>UseEmulator</c>) is not ported. Behaviour is unchanged.
/// </summary>
public static class TotkEmulatorHelper
{
    /// <summary>True when <paramref name="emulatorFilePath"/> is a Ryujinx executable.</summary>
    public static bool IsRyujinx(string emulatorFilePath) =>
        Path.GetFileNameWithoutExtension(emulatorFilePath)
            .Equals("ryujinx", StringComparison.InvariantCultureIgnoreCase);

    /// <summary>
    /// The emulator's TotK mod folder, <c>&lt;load dir&gt;/0100F2C0115B6000/TKMM</c>, or
    /// null (or empty) when it can't be found.
    /// </summary>
    public static string? GetModPath(string emulatorFilePath)
    {
        if (IsRyujinx(emulatorFilePath))
            return TotkRyujinxHelper.GetModPath(emulatorFilePath);

        if (TryGetEmulatorDataFolder(emulatorFilePath, out var emulatorDataFolderPath, out _) is null)
            return null;

        return GetModFolder(GetEmulatorConfigPath(emulatorDataFolderPath));
    }

    /// <summary>The emulator's SD card folder, or null when it can't be found.</summary>
    public static string? GetSdPath(string emulatorFilePath)
    {
        if (IsRyujinx(emulatorFilePath))
            return TotkRyujinxHelper.GetSdPath(emulatorFilePath);

        if (TryGetEmulatorDataFolder(emulatorFilePath, out var emulatorDataFolderPath, out _) is null)
            return null;

        return GetDirectoryFromConfig(GetEmulatorConfigPath(emulatorDataFolderPath), "sdmc_directory");
    }

    /// <summary>The emulator's NAND folder, or null when it can't be found.</summary>
    public static string? GetNandPath(string emulatorFilePath)
    {
        if (TryGetEmulatorDataFolder(emulatorFilePath, out var emulatorDataFolderPath, out _) is null)
            return null;

        return GetDirectoryFromConfig(GetEmulatorConfigPath(emulatorDataFolderPath), "nand_directory");
    }

    private static string? TryGetEmulatorDataFolder(string emulatorFilePath, out string emulatorDataFolderPath, out string emulatorName)
    {
        emulatorName = OperatingSystem.IsWindows()
            ? Path.GetFileNameWithoutExtension(emulatorFilePath)
            : emulatorFilePath.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(emulatorFilePath).ToLower()
                : Path.GetFileName(emulatorFilePath).ToLower();

        if (Path.GetDirectoryName(emulatorFilePath) is { } exeFolder
            && Path.Combine(exeFolder, "user") is var portableDataFolderPath
            && Directory.Exists(portableDataFolderPath))
        {
            emulatorDataFolderPath = portableDataFolderPath;
            var configPath = GetEmulatorConfigPath(emulatorDataFolderPath);
            if (File.Exists(configPath))
                return configPath;
        }

        if (OperatingSystem.IsWindows())
        {
            emulatorDataFolderPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), emulatorName);

            var emulatorConfigFilePath = GetConfigFilePath(emulatorName, emulatorDataFolderPath);
            return File.Exists(emulatorConfigFilePath) ? emulatorConfigFilePath : null;
        }
        else
        {
            var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                              ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

            emulatorDataFolderPath = Path.Combine(xdgDataHome, emulatorName);
            var emulatorConfigFilePath = GetConfigFilePath(emulatorName, emulatorDataFolderPath);
            return File.Exists(emulatorConfigFilePath) ? emulatorConfigFilePath : null;
        }
    }

    private static string GetEmulatorConfigPath(string emulatorDataFolderPath)
    {
        var emulatorName = Path.GetFileName(emulatorDataFolderPath);
        return GetConfigFilePath(emulatorName, emulatorDataFolderPath);
    }

    private static string GetModFolder(string emulatorConfigFilePath)
        => GetDirectoryFromConfig(emulatorConfigFilePath, "load_directory") is { } loadDir
            ? Path.Combine(loadDir, TotkTitle.TitleIdValue, TotkTitle.ModFolderName).Replace('\\', '/')
            : "";

    private static string? GetDirectoryFromConfig(string emulatorConfigFilePath, string configKey)
    {
        if (!File.Exists(emulatorConfigFilePath))
            return null;

        using var fs = File.OpenRead(emulatorConfigFilePath);
        using StreamReader reader = new(fs);

        while (reader.ReadLine() is { } line)
        {
            var trimmedLine = line.Trim();

            if (trimmedLine.StartsWith($"{configKey}=", StringComparison.OrdinalIgnoreCase))
            {
                var directoryPath = trimmedLine.Substring($"{configKey}=".Length).Trim();
                if (!string.IsNullOrEmpty(directoryPath) && Directory.Exists(directoryPath))
                    return directoryPath;
            }
        }

        return null;
    }

    private static string GetConfigFilePath(string emulatorName, string emulatorDataFolderPath)
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(emulatorDataFolderPath, "config", "qt-config.ini");

        var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(xdgConfigHome, emulatorName, "qt-config.ini");
    }
}
