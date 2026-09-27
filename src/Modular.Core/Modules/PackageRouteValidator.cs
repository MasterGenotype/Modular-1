using Modular.Sdk.Modules;

namespace Modular.Core.Modules;

/// <summary>
/// Checks a staged mod against a module's declarative package-route table.
/// </summary>
/// <remarks>
/// <para>
/// Runs for every module, so a module that declares its destinations as data gets these
/// checks without writing them. The one that matters in practice is the invented folder
/// level: a <see cref="PackageLayout.FlatFiles"/> destination breaks the moment a
/// <c>&lt;modname&gt;/</c> directory appears inside it. Cyberpunk's <c>archive/pc/mod</c> and
/// BG3's AppData <c>Mods</c> are both flat, and a mod wrapped in its own folder there loads
/// nothing at all while looking perfectly installed.
/// </para>
/// <para>
/// This is the same class of mistake as a nested <c>archive/pc/mod/&lt;sub&gt;/</c>, which the
/// Cyberpunk module already catches by hand for its analyzer-routed files.
/// </para>
/// </remarks>
public static class PackageRouteValidator
{
    /// <summary>
    /// Validates a staged mod against a module's route table.
    /// </summary>
    /// <param name="module">The module owning the game.</param>
    /// <param name="stagedMod">The staged mod.</param>
    /// <returns>
    /// Diagnostics, or none when the module declares no routes or nothing is wrong.
    /// </returns>
    public static IReadOnlyList<ModDiagnostic> Validate(IGameModule module, StagedMod stagedMod)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(stagedMod);

        IReadOnlyList<PackageRoute> routes;
        try
        {
            routes = module.GetPackageRoutes();
        }
        catch (Exception)
        {
            // A broken route table costs its own checks, not the install.
            return Array.Empty<ModDiagnostic>();
        }

        if (routes.Count == 0)
            return Array.Empty<ModDiagnostic>();

        var diagnostics = new List<ModDiagnostic>();

        foreach (var file in stagedMod.Files)
        {
            var route = FindRoute(routes, file);
            if (route is not { } matched)
                continue;

            if (matched.Layout is not (PackageLayout.FlatFiles or PackageLayout.PinnedLast))
                continue;

            var subPath = NormalizeSubPath(matched.SubPath);
            var relativePath = file.RelativePath.Replace('\\', '/');

            if (subPath.Length > 0)
            {
                if (!relativePath.StartsWith(subPath + "/", StringComparison.OrdinalIgnoreCase))
                    continue;

                relativePath = relativePath[(subPath.Length + 1)..];
            }

            // Flat means flat: anything left with a separator has an extra folder level.
            if (!relativePath.Contains('/'))
                continue;

            diagnostics.Add(new ModDiagnostic
            {
                Severity = DiagnosticSeverity.Error,
                Code = "route.flat_layout_extra_folder",
                Message =
                    $"'{file.RelativePath}' adds a folder level inside " +
                    $"{(subPath.Length == 0 ? matched.Root : $"{matched.Root}/{subPath}")}, " +
                    $"but {matched.PackageType} deploys as flat files. The game will not look " +
                    "inside a subfolder there, so this mod would do nothing.",
                RelativePath = file.RelativePath,
                Remedy = "Place the files directly in the destination, with no mod-named folder."
            });
        }

        return diagnostics;
    }

    /// <summary>
    /// Finds the route that applies to a staged file.
    /// </summary>
    /// <param name="routes">The module's route table.</param>
    /// <param name="file">The staged file, carrying its package type label.</param>
    /// <returns>The matching route, or <c>null</c> when none applies.</returns>
    /// <remarks>
    /// Matching is by package type, because the destination is a property of the package type
    /// rather than of the path: one game routinely sends different package types to different
    /// roots.
    /// </remarks>
    private static PackageRoute? FindRoute(
        IReadOnlyList<PackageRoute> routes, StagedFile file)
    {
        if (string.IsNullOrEmpty(file.PackageType))
            return null;

        foreach (var route in routes)
        {
            if (!string.Equals(route.PackageType, file.PackageType, StringComparison.OrdinalIgnoreCase))
                continue;

            var routeRoot = ModRoots.ResolveToken(route.Root);

            if (string.Equals(routeRoot, file.RootName, StringComparison.OrdinalIgnoreCase))
                return route;
        }

        return null;
    }

    private static string NormalizeSubPath(string? subPath) =>
        (subPath ?? string.Empty).Replace('\\', '/').Trim('/');
}
