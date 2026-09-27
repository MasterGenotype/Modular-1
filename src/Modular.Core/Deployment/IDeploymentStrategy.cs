using Modular.Sdk.Modules;

namespace Modular.Core.Deployment;

/// <summary>
/// Applies a deployment plan to a game.
/// </summary>
/// <remarks>
/// Extracted so a caller can count or intercept deployments. A snapshot restore in
/// particular has to reach its recorded state in exactly one deployment, and the only way
/// to hold that guarantee is to be able to count them.
/// </remarks>
public interface IDeployer
{
    /// <summary>
    /// Deploys a plan.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <param name="module">The module owning the game.</param>
    /// <param name="plan">The plan to apply.</param>
    /// <param name="settings">Deployment method and fallback.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What was placed, preserved, backed up and reported.</returns>
    Task<DeploymentResult> DeployAsync(
        GameInstallation installation,
        IGameModule module,
        DeploymentPlan plan,
        DeploymentSettings settings,
        CancellationToken ct = default);

    /// <summary>
    /// Removes everything Modular deployed for a game and restores the vanilla files.
    /// </summary>
    /// <param name="installation">The resolved installation.</param>
    /// <returns>What was removed, preserved and restored.</returns>
    DeploymentResult Purge(GameInstallation installation);

    /// <summary>
    /// Probes every method against every root of a plan.
    /// </summary>
    /// <param name="plan">A plan naming the roots to probe.</param>
    /// <returns>Per root, the methods that work.</returns>
    Dictionary<string, List<DeploymentMethod>> ProbeAll(DeploymentPlan plan);
}

/// <summary>
/// Places one staged file into a game root, and later recognises whether the file it
/// placed is still the one it placed.
/// </summary>
/// <remarks>
/// Purge deletes only files whose recorded identity still matches. A file whose identity
/// drifted was edited in place, or had its link replaced by an atomic save, and is
/// preserved rather than deleted — so a user edit is never silently destroyed.
/// </remarks>
public interface IDeploymentStrategy
{
    /// <summary>The method this strategy implements.</summary>
    DeploymentMethod Method { get; }

    /// <summary>
    /// Places a staged file at a destination, replacing whatever is there.
    /// </summary>
    /// <param name="stagedPath">Absolute path of the staged source file.</param>
    /// <param name="destinationPath">Absolute destination path. Its directory already exists.</param>
    /// <returns>The identity to record for the placed file.</returns>
    /// <exception cref="DeploymentMethodUnavailableException">
    /// The method cannot be used for this pair of paths — a cross-device hardlink, a
    /// symlink without privilege, a filesystem with no link support. The caller decides
    /// whether to fall back or abort.
    /// </exception>
    FileIdentity Place(string stagedPath, string destinationPath);

    /// <summary>
    /// Reads the current identity of a deployed file without following links.
    /// </summary>
    /// <param name="destinationPath">Absolute path of the deployed file.</param>
    /// <returns>The identity, or <c>null</c> when the file no longer exists.</returns>
    FileIdentity? ReadIdentity(string destinationPath);

    /// <summary>
    /// Whether a deployed file still matches what was recorded for it.
    /// </summary>
    /// <param name="recorded">Identity recorded at deploy time.</param>
    /// <param name="current">Identity read back now.</param>
    /// <returns>True when the file is unchanged and may be purged.</returns>
    bool Matches(FileIdentity recorded, FileIdentity current);

    /// <summary>
    /// Removes a deployed file.
    /// </summary>
    /// <param name="destinationPath">Absolute path of the deployed file.</param>
    /// <remarks>
    /// Must delete the entry itself and never write through it: a deployed file may share
    /// its bytes with the staged copy, and a symlink's target is the staged file.
    /// </remarks>
    void Remove(string destinationPath);

    /// <summary>
    /// Tests whether this method actually works between two directories, cleaning up
    /// after itself.
    /// </summary>
    /// <param name="stagingRoot">A directory files will be placed from.</param>
    /// <param name="destinationRoot">A root files will be placed into.</param>
    /// <returns>Null when the probe succeeded, otherwise why it failed.</returns>
    /// <remarks>
    /// Run once per resolved root before a deploy, so "documents is on another drive,
    /// those files will be copied" is reported before any game file is touched.
    /// </remarks>
    string? Probe(string stagingRoot, string destinationRoot);
}

/// <summary>
/// How a deployed file is recognised later.
/// </summary>
/// <param name="Method">Method that placed the file.</param>
/// <param name="Size">Size in bytes, or -1 when not applicable.</param>
/// <param name="ModifiedUtcTicks">Last write time in UTC ticks, or 0 when not applicable.</param>
/// <param name="Inode">Device and inode, for a hardlink.</param>
/// <param name="LinkTarget">Absolute link target, for a symlink.</param>
/// <param name="ContentHash">Content hash, for a copy.</param>
public readonly record struct FileIdentity(
    DeploymentMethod Method,
    long Size = -1,
    long ModifiedUtcTicks = 0,
    string? Inode = null,
    string? LinkTarget = null,
    string? ContentHash = null);

/// <summary>
/// Thrown when a deployment method cannot be used for a specific file.
/// </summary>
/// <remarks>
/// Distinct from a genuine I/O failure: this is the signal that drives the configured
/// fallback, so it must not be raised for a missing source or a full disk.
/// </remarks>
public sealed class DeploymentMethodUnavailableException : Exception
{
    /// <summary>
    /// Creates the exception.
    /// </summary>
    /// <param name="method">The method that is unavailable.</param>
    /// <param name="message">Why it is unavailable, phrased for the deploy report.</param>
    /// <param name="inner">The underlying failure, when there was one.</param>
    public DeploymentMethodUnavailableException(
        DeploymentMethod method, string message, Exception? inner = null)
        : base(message, inner)
    {
        Method = method;
    }

    /// <summary>The method that could not be used.</summary>
    public DeploymentMethod Method { get; }
}
