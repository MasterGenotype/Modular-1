namespace Modular.Core.Metadata;

/// <summary>
/// Says which category a mod hub files a mod under.
/// </summary>
/// <remarks>
/// Split into a lookup and a warm-up because the two are needed in different places. A
/// game module asks while it is planning a deployment, which must not wait on the
/// network, so <see cref="GetCategory"/> only ever answers from what is already known.
/// <see cref="WarmAsync"/> is the step that may go to the hub, and runs where waiting is
/// acceptable: when a mod is installed.
/// </remarks>
public interface IModCategorySource
{
    /// <summary>
    /// Returns the category a mod is filed under, if it is already known.
    /// </summary>
    /// <param name="gameDomain">Game domain on the hub.</param>
    /// <param name="modId">The mod's id on the hub.</param>
    /// <returns>The category's name, or <c>null</c> when it is not known.</returns>
    string? GetCategory(string gameDomain, string modId);

    /// <summary>
    /// Looks up the categories of the given mods so later lookups can answer.
    /// </summary>
    /// <param name="gameDomain">Game domain on the hub.</param>
    /// <param name="modIds">Mod ids to look up.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>Failures are swallowed: a category is an aid to ordering, never a requirement.</remarks>
    Task WarmAsync(string gameDomain, IReadOnlyList<string> modIds, CancellationToken ct = default);
}
