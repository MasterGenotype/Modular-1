using Modular.Core.Archives;
using Modular.Core.Installers.HorizonZeroDawn;
using Modular.Sdk;
using Modular.Sdk.Archives;
using Modular.Sdk.Installers;
using Modular.Sdk.Modules;

namespace Modular.Core.Modules;

/// <summary>
/// Game module for Horizon Zero Dawn Complete Edition (Steam AppID 1151640).
/// </summary>
/// <remarks>
/// Decima-engine mods are <c>Patch_*.bin</c> files in <c>Packed_DX12/</c>, applied in
/// alphabetical order with the later patch winning. Horizon Zero Dawn <em>Remastered</em>
/// (AppID 2561580) has no such workflow — its mods are root-level DLL loaders — so its
/// executable is a rejecting anchor and Remastered falls through to
/// <see cref="GenericGameModule"/>, whose loose-file installer fits it.
/// </remarks>
public sealed class HorizonZeroDawnGameModule : GameModuleBase
{
    private readonly IArchiveReaderFactory _archiveReaderFactory;
    private readonly bool _orderPrefixEnabled;

    /// <summary>The patch folder, relative to the game root.</summary>
    public const string PackedFolder = "Packed_DX12";

    /// <summary>
    /// The prefetch patch several mods ship and tools regenerate.
    /// </summary>
    /// <remarks>
    /// A routine file-level collision rather than a mod bug: whichever mod is later wins the
    /// file, and the correct fix is to regenerate the prefetch afterwards, not to reorder.
    /// </remarks>
    public const string PrefetchPatch = "Patch_zzzzPrefetch.bin";

    /// <summary>
    /// Creates the module.
    /// </summary>
    /// <param name="archiveReaderFactory">Shared archive reader factory.</param>
    /// <param name="orderPrefixEnabled">
    /// Whether to encode load order into <c>Patch_*.bin</c> filenames. Off by default: it is
    /// unverified whether the engine requires the literal <c>Patch_</c> prefix, and getting
    /// that wrong would silently stop every patch loading.
    /// </param>
    public HorizonZeroDawnGameModule(
        IArchiveReaderFactory? archiveReaderFactory = null,
        bool orderPrefixEnabled = false)
    {
        _archiveReaderFactory = archiveReaderFactory ?? new ArchiveReaderFactory();
        _orderPrefixEnabled = orderPrefixEnabled;
    }

    /// <inheritdoc />
    public override string ModuleId => "horizonzerodawn";

    /// <inheritdoc />
    public override string DisplayName => "Horizon Zero Dawn Complete Edition";

    /// <inheritdoc />
    public override IReadOnlyList<string> GameIds { get; } =
        [Sdk.GameIds.HorizonZeroDawn, "1151640"];

    /// <inheritdoc />
    public override IReadOnlyList<int> SteamAppIds { get; } = [1151640];

    /// <inheritdoc />
    protected override IReadOnlyList<string> PrimaryAnchors { get; } =
        ["HorizonZeroDawn.exe", "Packed_DX12"];

    /// <summary>
    /// Horizon Zero Dawn Remastered's executable. Remastered uses a completely different mod
    /// layout, so it must never be claimed by this module.
    /// </summary>
    protected override IReadOnlyList<string> RejectingAnchors { get; } =
        ["HorizonZeroDawnRemastered.exe"];

    /// <inheritdoc />
    public override IReadOnlyList<IModInstaller> GetInstallers() =>
        [new HZDModInstaller(_archiveReaderFactory)];

    /// <summary>
    /// Whether order prefixing is on for this instance.
    /// </summary>
    public bool OrderPrefixEnabled => _orderPrefixEnabled;

    /// <summary>
    /// Encodes load order into patch filenames, when the option is on.
    /// </summary>
    /// <param name="context">The staged file plus its ordering information.</param>
    /// <returns>Where the file should actually be deployed.</returns>
    /// <remarks>
    /// The rename keeps the literal <c>Patch_</c> prefix and inserts the order after it
    /// (<c>Patch_007_Foo.bin</c>), because it is unverified whether the engine requires that
    /// prefix. Disabled by default for the same reason.
    /// </remarks>
    public override DeployTarget MapDeployPath(DeployPathContext context)
    {
        if (!_orderPrefixEnabled)
            return new DeployTarget(context.RootName, context.RelativePath);

        if (!string.Equals(context.RootName, ModRoots.Game, StringComparison.OrdinalIgnoreCase))
            return new DeployTarget(context.RootName, context.RelativePath);

        var relativePath = context.RelativePath.Replace('\\', '/');
        var folder = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? string.Empty;

        if (!string.Equals(folder, PackedFolder, StringComparison.OrdinalIgnoreCase))
            return new DeployTarget(context.RootName, relativePath);

        var fileName = Path.GetFileName(relativePath);

        if (!fileName.StartsWith("Patch_", StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            return new DeployTarget(context.RootName, relativePath);
        }

        var width = Math.Max(3, context.EnabledModCount.ToString().Length);
        var prefix = context.OrderIndex.ToString(
            new string('0', width), System.Globalization.CultureInfo.InvariantCulture);

        var remainder = fileName["Patch_".Length..];

        return new DeployTarget(context.RootName, $"{folder}/Patch_{prefix}_{remainder}");
    }

    /// <inheritdoc />
    public override EngineOrderDescription? DescribeEngineOrder(
        GameInstallation installation,
        IReadOnlyList<DeployedMod> mods)
    {
        var patches = mods
            .SelectMany(m => m.DeployedFiles)
            .Where(f =>
                string.Equals(f.RootName, ModRoots.Game, StringComparison.OrdinalIgnoreCase) &&
                f.RelativePath.StartsWith(PackedFolder, StringComparison.OrdinalIgnoreCase) &&
                f.RelativePath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetFileName(f.RelativePath))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        if (patches.Count == 0)
            return null;

        return new EngineOrderDescription(
            "alphabetical; the later patch wins",
            patches,
            _orderPrefixEnabled
                ? "filename prefixes"
                : "the filenames the mod authors chose (Modular's prefixing is off)");
    }

    /// <summary>
    /// Flags the prefetch collision every multi-mod HZD setup runs into.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <param name="stagedMod">The staged mod.</param>
    /// <returns>Diagnostics; a warning, never an error.</returns>
    public override IReadOnlyList<ModDiagnostic> ValidateStaged(
        GameInstallation installation,
        StagedMod stagedMod)
    {
        ArgumentNullException.ThrowIfNull(stagedMod);

        var diagnostics = new List<ModDiagnostic>();

        foreach (var file in stagedMod.Files)
        {
            if (!string.Equals(file.RootName, ModRoots.Game, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!Path.GetFileName(file.RelativePath)
                    .Equals(PrefetchPatch, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            diagnostics.Add(ModDiagnostic.Warning(
                "hzd.prefetch_conflict",
                $"This mod ships {PrefetchPatch}, which several mods and tools also write. " +
                "The last mod in the order wins it; regenerate the prefetch file afterwards " +
                "so it matches your full mod set.",
                file.RelativePath));
        }

        return diagnostics;
    }
}
