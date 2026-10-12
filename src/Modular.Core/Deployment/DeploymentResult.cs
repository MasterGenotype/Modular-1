using Modular.Sdk.Modules;

namespace Modular.Core.Deployment;

/// <summary>
/// What a deployment actually did, in enough detail to explain it to the user.
/// </summary>
public sealed class DeploymentResult
{
    /// <summary>Whether the deployment completed.</summary>
    public bool Success { get; set; }

    /// <summary>Key of the game deployed to.</summary>
    public string GameKey { get; set; } = string.Empty;

    /// <summary>The method that was asked for.</summary>
    public DeploymentMethod RequestedMethod { get; set; } = DeploymentMethod.Hardlink;

    /// <summary>Where that method came from: a flag, a per-game setting, config or the default.</summary>
    public string MethodSource { get; set; } = "default";

    /// <summary>Files newly placed or rewritten, as <c>root/path</c>.</summary>
    public List<string> Placed { get; } = new();

    /// <summary>Files already correct and left alone, as <c>root/path</c>.</summary>
    public List<string> Unchanged { get; } = new();

    /// <summary>Files removed because no enabled mod ships them any more.</summary>
    public List<string> Removed { get; } = new();

    /// <summary>Vanilla files put back after their mod was removed.</summary>
    public List<string> Restored { get; } = new();

    /// <summary>Pre-existing unmanaged files whose vanilla copy was backed up.</summary>
    public List<string> VanillaBackedUp { get; } = new();

    /// <summary>Destinations that held a file Modular had not deployed.</summary>
    public List<string> ReplacedUnmanaged { get; } = new();

    /// <summary>
    /// Files that had changed since deployment and were moved into the overwrite store
    /// instead of being deleted.
    /// </summary>
    public List<string> Displaced { get; } = new();

    /// <summary>Files placed by copying because the chosen method was unavailable.</summary>
    public List<string> FellBackToCopy { get; } = new();

    /// <summary>Files whose method was overridden by a module constraint.</summary>
    public List<string> ConstraintApplied { get; } = new();

    /// <summary>Generated paths that purge deliberately left untouched.</summary>
    public List<string> GeneratedPathsLeftAlone { get; } = new();

    /// <summary>
    /// Files whose vanilla backup was not restored because it belongs to an older build.
    /// </summary>
    public List<string> StaleBackupsSkipped { get; } = new();

    /// <summary>Non-fatal findings worth showing the user.</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>Failure reason, when the deployment did not complete.</summary>
    public string? Error { get; set; }

    /// <summary>The manifest that was written.</summary>
    public DeploymentManifest? Manifest { get; set; }

    /// <summary>The plan that was applied, for reporting overlaps and skipped files.</summary>
    public DeploymentPlan? Plan { get; set; }

    /// <summary>Mods whose staging tree was deleted after a successful redeploy.</summary>
    public List<string> StagingRemoved { get; } = new();

    /// <summary>The mods that are now deployed, in load order.</summary>
    public IReadOnlyList<DeployedMod> DeployedMods { get; set; } = Array.Empty<DeployedMod>();

    /// <summary>What the user must do for the deployed set to load.</summary>
    public LaunchRequirements LaunchRequirements { get; set; } = LaunchRequirements.None;

    /// <summary>
    /// DLL names needing a native-first Wine override, from the module and from proxy-DLL
    /// detection combined.
    /// </summary>
    public List<string> WineDllOverrides { get; set; } = new();

    /// <summary>
    /// The <c>WINEDLLOVERRIDES</c> launch-option prefix to recommend, or <c>null</c> when
    /// none is needed.
    /// </summary>
    public string? WineDllOverridesLaunchOption =>
        WineDllOverrides.Count == 0
            ? null
            : $"WINEDLLOVERRIDES=\"{string.Join(",", WineDllOverrides)}=n,b\" %command%";
}

/// <summary>
/// Finds proxy DLLs in a deployment that only load under Proton with a native-first
/// override.
/// </summary>
/// <remarks>
/// Deliberately generic rather than per-module: every loader in this family works the same
/// way, by shipping a DLL named after a system library next to the game executable. RED4ext
/// and CET use <c>winmm</c>/<c>version</c>, BepInEx uses <c>winhttp</c>, FF7R hooks use
/// <c>dxgi</c>/<c>xinput1_3</c>/<c>dinput8</c>, and DXVK uses <c>d3d11</c>/<c>dxgi</c>.
/// </remarks>
public static class ProxyDllDetector
{
    /// <summary>
    /// System DLL names a loader may impersonate.
    /// </summary>
    public static IReadOnlyList<string> KnownProxyNames { get; } =
    [
        "winmm", "version", "winhttp", "dxgi", "d3d11", "dinput8", "xinput1_3", "dsound",
        "dwrite"
    ];

    /// <summary>
    /// Detects proxy DLLs a plan places next to a game executable.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <param name="plan">The deployment plan.</param>
    /// <returns>
    /// Proxy DLL names, or an empty list when none apply. Empty unless the game runs
    /// through Proton, since a native Windows install needs no override.
    /// </returns>
    public static IReadOnlyList<string> Detect(GameInstallation installation, DeploymentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(plan);

        if (!installation.IsProton)
            return Array.Empty<string>();

        var names = new List<string>();

        foreach (var file in plan.Files)
        {
            if (!string.Equals(file.RootName, ModRoots.Game, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!file.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                continue;

            var stem = Path.GetFileNameWithoutExtension(file.RelativePath);
            if (!KnownProxyNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
                continue;

            // Only a DLL sitting beside an executable is loaded as a proxy.
            var folder = Path.GetDirectoryName(file.DestinationPath);
            if (folder == null || !HasExecutable(folder))
                continue;

            names.Add(stem.ToLowerInvariant());
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool HasExecutable(string folder)
    {
        try
        {
            return Directory.Exists(folder) &&
                   Directory.EnumerateFiles(folder, "*.exe").Any();
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
