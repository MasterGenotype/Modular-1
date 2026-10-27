namespace Modular.Core.Deployment;

/// <summary>
/// How the deployer places a staged file into a game root.
/// </summary>
/// <remarks>
/// The trade-offs are real and none of the three dominates, so the user picks. The
/// planner, purge, manifest and displaced-file handling are shared; only the placement
/// and the identity check differ.
/// </remarks>
public enum DeploymentMethod
{
    /// <summary>
    /// A second directory entry for the same data. No extra disk use, indistinguishable
    /// from a normal file, and survives the staging folder being moved — but staging and
    /// the target must share a filesystem.
    /// </summary>
    Hardlink,

    /// <summary>
    /// A link that points at the absolute staged path. Works across drives, but is
    /// visibly a link, needs Developer Mode on Windows, and dangles if staging moves.
    /// </summary>
    Symlink,

    /// <summary>
    /// A full second copy. Works everywhere and isolates the staged bytes from anything
    /// the game or a tool writes, at the cost of disk space and time.
    /// </summary>
    Copy
}

/// <summary>
/// What to do when the chosen <see cref="DeploymentMethod"/> cannot be used for a file.
/// </summary>
public enum DeploymentFallback
{
    /// <summary>Place the file by copying it instead. The default.</summary>
    Copy,

    /// <summary>Abort the deployment before touching the game directory.</summary>
    Fail
}

/// <summary>
/// The resolved deployment method and fallback for one game.
/// </summary>
/// <param name="Method">Method to attempt for each file.</param>
/// <param name="Fallback">What to do when the method fails for a file.</param>
/// <param name="Source">Where the method came from, for the deploy report.</param>
public readonly record struct DeploymentSettings(
    DeploymentMethod Method,
    DeploymentFallback Fallback,
    string Source)
{
    /// <summary>The built-in default: hardlink, falling back to copy.</summary>
    public static DeploymentSettings Default { get; } =
        new(DeploymentMethod.Hardlink, DeploymentFallback.Copy, "default");

    /// <summary>
    /// Whether every file is laid down again from staging, even one already in place.
    /// Files changed outside Modular are still preserved before they are replaced.
    /// </summary>
    public bool Force { get; init; }
}

/// <summary>
/// Parsing helpers for the deployment enums, shared by the CLI, config and database.
/// </summary>
public static class DeploymentMethods
{
    /// <summary>
    /// Parses a method name.
    /// </summary>
    /// <param name="value">Name such as <c>hardlink</c>, <c>symlink</c> or <c>copy</c>.</param>
    /// <returns>The method, or <c>null</c> when the name is not recognised.</returns>
    public static DeploymentMethod? ParseMethod(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "hardlink" or "hard" or "link" => DeploymentMethod.Hardlink,
            "symlink" or "sym" or "symbolic" => DeploymentMethod.Symlink,
            "copy" => DeploymentMethod.Copy,
            _ => null
        };

    /// <summary>
    /// Parses a fallback name.
    /// </summary>
    /// <param name="value">Name such as <c>copy</c> or <c>fail</c>.</param>
    /// <returns>The fallback, or <c>null</c> when the name is not recognised.</returns>
    public static DeploymentFallback? ParseFallback(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "copy" => DeploymentFallback.Copy,
            "fail" or "abort" => DeploymentFallback.Fail,
            _ => null
        };

    /// <summary>
    /// Renders a method for storage and display.
    /// </summary>
    /// <param name="method">The method.</param>
    /// <returns>Its lowercase name.</returns>
    public static string ToName(this DeploymentMethod method) =>
        method.ToString().ToLowerInvariant();

    /// <summary>
    /// Renders a fallback for storage and display.
    /// </summary>
    /// <param name="fallback">The fallback.</param>
    /// <returns>Its lowercase name.</returns>
    public static string ToName(this DeploymentFallback fallback) =>
        fallback.ToString().ToLowerInvariant();
}
