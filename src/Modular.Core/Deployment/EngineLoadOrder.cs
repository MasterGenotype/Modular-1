using Modular.Sdk.Modules;

namespace Modular.Core.Deployment;

/// <summary>
/// Asks a module how its engine will order a deployed mod set, without letting a broken
/// module break the caller.
/// </summary>
public static class EngineLoadOrder
{
    /// <summary>
    /// Describes the engine order for a deployed set.
    /// </summary>
    /// <param name="module">The module owning the game.</param>
    /// <param name="installation">The resolved installation.</param>
    /// <param name="mods">Deployed mods in Modular order, later winning.</param>
    /// <returns>
    /// The description, or <c>null</c> when the module has no separate engine order to
    /// report or threw while producing one.
    /// </returns>
    public static EngineOrderDescription? Describe(
        IGameModule module,
        GameInstallation installation,
        IReadOnlyList<DeployedMod> mods)
    {
        ArgumentNullException.ThrowIfNull(module);

        try
        {
            return module.DescribeEngineOrder(installation, mods);
        }
        catch (Exception)
        {
            // Purely informational: a module that cannot describe its order must not stop
            // the user seeing Modular's.
            return null;
        }
    }
}
