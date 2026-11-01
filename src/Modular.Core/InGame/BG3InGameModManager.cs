using System.Globalization;
using Microsoft.Extensions.Logging;
using Modular.Sdk.Backends;
using Modular.Core.Installers.BaldursGate3;
using Modular.Core.Modules;
using Modular.Sdk.Modules;

namespace Modular.Core.InGame;

/// <summary>
/// Baldur's Gate 3's in-game mod manager, which installs the user's mod.io subscriptions.
/// </summary>
/// <remarks>
/// <para>
/// Verified on the game's files: the manager takes the pak out of the mod.io upload's zip
/// unchanged and saves it in the user <c>Mods</c> folder under the zip's own name, with
/// <c>.pak</c> for <c>.zip</c> (<c>alfirajoinstheparty_3539eba9-6-6gki.zip</c> becomes
/// <c>alfirajoinstheparty_3539eba9-6-6gki.pak</c>). The name changes with every upload. A
/// mod.io pak names its mod in its own <c>meta.lsx</c>: <c>PublishHandle</c> is the mod.io
/// mod id. The manager keeps no other state on disk.
/// </para>
/// <para>
/// The <c>MD5</c> a mod.io pak's metadata records is not a hash of the pak, and two
/// different mods' uploads can share one, so it is never used on its own to tell uploads
/// apart.
/// </para>
/// </remarks>
public sealed class BG3InGameModManager : IInGameModManager
{
    /// <summary>Baldur's Gate 3's id on mod.io.</summary>
    public const string ModIoGameId = "6715";

    private readonly Func<bool> _isGameRunning;
    private readonly ILogger? _logger;

    /// <summary>Creates the adapter.</summary>
    /// <param name="isGameRunning">Says whether the game runs; replaceable for tests.</param>
    /// <param name="logger">Optional logger.</param>
    public BG3InGameModManager(Func<bool>? isGameRunning = null, ILogger? logger = null)
    {
        _isGameRunning = isGameRunning ?? BaldursGate3GameModule.DetectRunningGame;
        _logger = logger;
    }

    /// <inheritdoc />
    public string BackendId => BackendIds.ModIo;

    /// <inheritdoc />
    public string RemoteGameId => ModIoGameId;

    /// <inheritdoc />
    public bool IsGameRunning() => _isGameRunning();

    /// <inheritdoc />
    public string FileNameFor(string uploadFileName) =>
        Path.GetFileNameWithoutExtension(uploadFileName) + ".pak";

    /// <inheritdoc />
    public IReadOnlyList<InGameModFile> Scan(GameInstallation installation)
    {
        var modsDirectory = ModsDirectory(installation);
        if (modsDirectory == null || !Directory.Exists(modsDirectory))
            return [];

        var files = new List<InGameModFile>();
        foreach (var path in Directory.EnumerateFiles(modsDirectory, "*.pak").Order(StringComparer.Ordinal))
        {
            BG3PakMeta? meta;
            try
            {
                meta = BG3PakMeta.Read(path);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _logger?.LogDebug(ex, "Could not read {Pak}", path);
                continue;
            }

            if (meta == null)
                continue;

            var name = Path.GetFileName(path);
            var module = meta.Module;
            files.Add(new InGameModFile(
                name,
                path,
                module.Uuid.ToLowerInvariant(),
                module.Name,
                module.PublishHandle == 0 ? null : module.PublishHandle.ToString(CultureInfo.InvariantCulture),
                module.Version64,
                module.Md5,
                BaldursGate3GameModule.ExistingOrderPrefix().IsMatch(name)));
        }

        return files;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Remove(GameInstallation installation, InGameModFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (IsGameRunning())
            throw new InvalidOperationException("Baldur's Gate 3 is running; close it before removing mods.");

        var modsDirectory = ModsDirectory(installation)
            ?? throw new InvalidOperationException("Baldur's Gate 3's Mods folder could not be found.");

        // Only ever a file directly in the Mods folder, whatever the record says.
        var path = Path.Combine(modsDirectory, Path.GetFileName(file.FileName));
        var removed = new List<string>();

        if (File.Exists(path))
        {
            File.Delete(path);
            removed.Add(file.FileName);
        }

        var modSettings = BaldursGate3GameModule.ResolveModSettingsPath(installation);
        if (modSettings != null)
        {
            // Another pak of the same module keeps the entry: it is that pak's now.
            var stillThere = Scan(installation).Any(f => f.ModuleUuid == file.ModuleUuid);
            if (!stillThere)
            {
                removed.AddRange(BG3ModSettings.RemoveEntries(modSettings, [file.ModuleUuid], () => !IsGameRunning())
                    .Select(n => $"load order entry '{n}'"));
            }
        }

        _logger?.LogInformation("Removed game-owned mod {File}: {Removed}", file.FileName, string.Join(", ", removed));
        return removed;
    }

    private static string? ModsDirectory(GameInstallation installation)
    {
        var userData = BaldursGate3GameModule.ResolveUserDataDirectory(installation);
        return userData == null ? null : Path.Combine(userData, "Mods");
    }
}
