namespace Modular.Sdk.Backends;

/// <summary>
/// Optional interface for backends that know which other mods a mod requires.
/// Backends declare support via <see cref="BackendCapabilities.Requirements"/>.
/// </summary>
public interface IRequirementsBackend
{
    /// <summary>
    /// Fetches the requirements the authors of the given mods have listed.
    /// </summary>
    /// <param name="modIds">Mod ids to look up, all for the same game.</param>
    /// <param name="gameDomain">
    /// Game domain the mods belong to (required for backends with GameDomains capability).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Requirements keyed by the requiring mod's id. A mod the backend does not know is
    /// absent; a mod with no requirements maps to an empty list.
    /// </returns>
    Task<IReadOnlyDictionary<string, IReadOnlyList<ModRequirement>>> GetRequirementsAsync(
        IReadOnlyList<string> modIds,
        string? gameDomain = null,
        CancellationToken ct = default);
}

/// <summary>
/// One mod that another mod's author says it requires.
/// </summary>
public record ModRequirement
{
    /// <summary>
    /// Id of the required mod on the same backend. Empty when <see cref="IsExternal"/>.
    /// </summary>
    public string ModId { get; init; } = string.Empty;

    /// <summary>Name of the required mod.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Whether the requirement is for the same game as the mod that lists it. A backend
    /// may list a requirement that lives under another game.
    /// </summary>
    public bool SameGame { get; init; } = true;

    /// <summary>
    /// Whether the requirement lives off the backend, such as a tool on another site.
    /// It cannot be downloaded through the backend; <see cref="Url"/> says where it is.
    /// </summary>
    public bool IsExternal { get; init; }

    /// <summary>Where to get the requirement, when the backend gives a link.</summary>
    public string? Url { get; init; }

    /// <summary>
    /// The author's note, which is often the only thing saying which file is needed.
    /// </summary>
    public string? Notes { get; init; }
}
