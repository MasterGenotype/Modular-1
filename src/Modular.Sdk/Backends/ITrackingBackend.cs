using Modular.Sdk.Backends.Common;

namespace Modular.Sdk.Backends;

/// <summary>
/// Optional interface for backends whose user list of mods can be changed: NexusMods
/// tracked mods, mod.io subscriptions. Backends declare support via
/// <see cref="BackendCapabilities.Tracking"/>.
/// </summary>
/// <remarks>
/// The list is the one <see cref="IModBackend.GetUserModsAsync"/> reads. Tracking a mod
/// that is already tracked, or untracking one that is not, succeeds and says so in the
/// outcome, so a batch can be run again safely.
/// </remarks>
public interface ITrackingBackend
{
    /// <summary>What the backend calls tracking, for messages and buttons.</summary>
    TrackingTerms Terms { get; }

    /// <summary>
    /// Adds a mod to the user's list.
    /// </summary>
    /// <param name="modId">The mod's id on the backend.</param>
    /// <param name="gameDomain">
    /// Game the mod belongs to (required for backends with GameDomains capability).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see cref="TrackOutcome.Tracked"/> or <see cref="TrackOutcome.AlreadyTracked"/>.</returns>
    /// <exception cref="TrackingException">The backend refused the change.</exception>
    Task<TrackResult> TrackAsync(string modId, string? gameDomain = null, CancellationToken ct = default);

    /// <summary>
    /// Removes a mod from the user's list.
    /// </summary>
    /// <param name="modId">The mod's id on the backend.</param>
    /// <param name="gameDomain">
    /// Game the mod belongs to (required for backends with GameDomains capability).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see cref="TrackOutcome.Untracked"/> or <see cref="TrackOutcome.NotTracked"/>.</returns>
    /// <exception cref="TrackingException">The backend refused the change.</exception>
    Task<TrackResult> UntrackAsync(string modId, string? gameDomain = null, CancellationToken ct = default);

    /// <summary>
    /// Gets the ids of every mod on the user's list, without fetching each mod's details.
    /// </summary>
    /// <param name="gameDomain">Only this game's mods, or every game's when null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The mod ids.</returns>
    Task<IReadOnlySet<string>> GetTrackedIdsAsync(string? gameDomain = null, CancellationToken ct = default);
}

/// <summary>
/// The words a backend uses for tracking.
/// </summary>
/// <param name="Verb">"Track" or "Subscribe".</param>
/// <param name="UndoVerb">"Untrack" or "Unsubscribe".</param>
/// <param name="ListName">"Tracked" or "Subscribed".</param>
public record TrackingTerms(string Verb, string UndoVerb, string ListName)
{
    /// <summary>NexusMods' words.</summary>
    public static TrackingTerms Track { get; } = new("Track", "Untrack", "Tracked");

    /// <summary>mod.io's words.</summary>
    public static TrackingTerms Subscribe { get; } = new("Subscribe", "Unsubscribe", "Subscribed");
}

/// <summary>What a track or untrack request did.</summary>
public enum TrackOutcome
{
    /// <summary>The mod was added to the list.</summary>
    Tracked,

    /// <summary>The mod was on the list already.</summary>
    AlreadyTracked,

    /// <summary>The mod was removed from the list.</summary>
    Untracked,

    /// <summary>The mod was not on the list.</summary>
    NotTracked,
}

/// <summary>
/// The result of a track or untrack request.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Mod">The mod, when the backend returned it.</param>
public record TrackResult(TrackOutcome Outcome, BackendMod? Mod = null)
{
    /// <summary>Whether the request changed the list.</summary>
    public bool Changed => Outcome is TrackOutcome.Tracked or TrackOutcome.Untracked;
}

/// <summary>Why a backend refused a track or untrack request.</summary>
public enum TrackingFailure
{
    /// <summary>No credentials, or the credentials have expired.</summary>
    NotAuthenticated,

    /// <summary>The credentials can read the list but not change it.</summary>
    MissingWriteScope,

    /// <summary>The backend has no such mod.</summary>
    ModNotFound,

    /// <summary>The mod exists but cannot be tracked: hidden, taken down, or the account is inactive.</summary>
    Unavailable,

    /// <summary>Too many requests; try again later.</summary>
    RateLimited,

    /// <summary>Anything else.</summary>
    Other,
}

/// <summary>
/// A refused track or untrack request, with a reason the CLI and GUI can act on without
/// knowing which backend refused it.
/// </summary>
public sealed class TrackingException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TrackingException(TrackingFailure reason, string message, Exception? inner = null)
        : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>Why the request was refused.</summary>
    public TrackingFailure Reason { get; }
}
