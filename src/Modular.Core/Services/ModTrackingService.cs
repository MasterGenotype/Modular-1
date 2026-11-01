using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Modular.Core.Backends.ModIo;
using Modular.Sdk.Backends;

namespace Modular.Core.Services;

/// <summary>
/// A mod as the user named it: an id, or a link to its page.
/// </summary>
/// <param name="BackendId">The hub the link points at, or null for a bare id.</param>
/// <param name="GameDomain">The game the link names, or null.</param>
/// <param name="ModId">The mod's id, or its page name when <paramref name="IsPageName"/>.</param>
/// <param name="IsPageName">Whether <paramref name="ModId"/> still has to be looked up.</param>
public sealed record ModReference(string? BackendId, string? GameDomain, string ModId, bool IsPageName);

/// <summary>One mod's result in a batch.</summary>
/// <param name="Input">What the user gave.</param>
/// <param name="ModId">The resolved mod id, when it resolved.</param>
/// <param name="Result">What the hub did, on success.</param>
/// <param name="Failure">Why it failed, on failure.</param>
/// <param name="Error">The failure's message.</param>
public sealed record TrackingItemResult(
    string Input,
    string? ModId,
    TrackResult? Result,
    TrackingFailure? Failure = null,
    string? Error = null)
{
    /// <summary>Whether the hub accepted the request.</summary>
    public bool Succeeded => Result != null;
}

/// <summary>
/// Tracks and untracks mods on any backend that supports it, for the CLI and the GUI alike.
/// </summary>
/// <remarks>
/// A batch carries on past a mod that fails, except when the failure would repeat for
/// every mod: missing or read-only credentials.
/// </remarks>
public sealed partial class ModTrackingService
{
    private readonly ILogger? _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="logger">Optional logger.</param>
    public ModTrackingService(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Reads a mod id or a NexusMods or mod.io page link.
    /// </summary>
    /// <param name="input">What the user typed.</param>
    /// <returns>The reference.</returns>
    /// <exception cref="ArgumentException">The input is neither.</exception>
    public static ModReference Parse(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var text = input.Trim();

        if (NexusLink().Match(text) is { Success: true } nexus)
            return new ModReference(BackendIds.NexusMods, nexus.Groups["game"].Value, nexus.Groups["id"].Value, false);

        if (ModIoLink().Match(text) is { Success: true } modio)
            return new ModReference(BackendIds.ModIo, null, modio.Groups["name"].Value, true);

        if (text.Length > 0 && text.All(char.IsAsciiDigit))
            return new ModReference(null, null, text, false);

        throw new ArgumentException(
            $"'{input}' is not a mod id or a NexusMods or mod.io mod page link.", nameof(input));
    }

    /// <summary>
    /// Tracks or untracks each mod in turn.
    /// </summary>
    /// <param name="backend">The backend.</param>
    /// <param name="inputs">Mod ids or page links, all for <paramref name="backend"/>.</param>
    /// <param name="gameDomain">The game, for backends that need one.</param>
    /// <param name="track">True to track, false to untrack.</param>
    /// <param name="before">
    /// Called with each resolved mod id before its request; returning false skips the mod.
    /// </param>
    /// <param name="after">Called with each result as it arrives.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>One result per input, in order.</returns>
    public async Task<IReadOnlyList<TrackingItemResult>> ApplyAsync(
        IModBackend backend,
        IReadOnlyList<string> inputs,
        string? gameDomain,
        bool track,
        Func<string, CancellationToken, Task<bool>>? before = null,
        Func<TrackingItemResult, CancellationToken, Task>? after = null,
        CancellationToken ct = default)
    {
        if (backend is not ITrackingBackend tracking)
            throw new InvalidOperationException($"{backend.DisplayName} does not support tracking.");

        var results = new List<TrackingItemResult>();
        var stop = false;

        foreach (var input in inputs)
        {
            ct.ThrowIfCancellationRequested();

            TrackingItemResult item;
            if (stop)
            {
                item = new TrackingItemResult(input, null, null, TrackingFailure.NotAuthenticated, "skipped");
                results.Add(item);
                continue;
            }

            string? modId = null;
            try
            {
                (modId, var game) = await ResolveAsync(backend, input, gameDomain, ct);
                if (before != null && !await before(modId, ct))
                {
                    item = new TrackingItemResult(input, modId, null, TrackingFailure.Other, "skipped");
                }
                else
                {
                    var result = track
                        ? await tracking.TrackAsync(modId, game, ct)
                        : await tracking.UntrackAsync(modId, game, ct);
                    item = new TrackingItemResult(input, modId, result);
                }
            }
            catch (TrackingException ex)
            {
                item = new TrackingItemResult(input, modId, null, ex.Reason, ex.Message);
                stop = ex.Reason is TrackingFailure.NotAuthenticated or TrackingFailure.MissingWriteScope;
            }
            catch (ArgumentException ex)
            {
                item = new TrackingItemResult(input, modId, null, TrackingFailure.ModNotFound, ex.Message);
            }
            catch (Exception ex) when (ex is HttpRequestException or ModIoException or InvalidOperationException)
            {
                item = new TrackingItemResult(input, modId, null, TrackingFailure.Other, ex.Message);
            }

            _logger?.LogDebug("{Verb} {Input}: {Outcome}", track ? "Track" : "Untrack", input,
                item.Result?.Outcome.ToString() ?? item.Error);
            results.Add(item);
            if (after != null)
                await after(item, ct);
        }

        return results;
    }

    // The mod id behind an input, looking up a mod.io page name, and the game: the one
    // given, else the one a NexusMods link names.
    private static async Task<(string ModId, string? GameDomain)> ResolveAsync(
        IModBackend backend, string input, string? gameDomain, CancellationToken ct)
    {
        var reference = Parse(input);

        if (reference.BackendId != null &&
            !string.Equals(reference.BackendId, backend.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{input}' is a {reference.BackendId} link, not {backend.Id}.");
        }

        if (reference.GameDomain != null && gameDomain != null &&
            !string.Equals(reference.GameDomain, gameDomain, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{input}' is for {reference.GameDomain}, not {gameDomain}.");
        }

        if (!reference.IsPageName)
            return (reference.ModId, gameDomain ?? reference.GameDomain);

        if (backend is not ModIoBackend modIo)
            throw new ArgumentException($"'{input}' cannot be looked up on {backend.DisplayName}.");

        var modId = await modIo.FindModIdAsync(reference.ModId, gameDomain, ct)
            ?? throw new ArgumentException($"No mod.io mod '{reference.ModId}' in the configured games.");
        return (modId, gameDomain);
    }

    [GeneratedRegex(@"^(?:https?://)?(?:www\.)?nexusmods\.com/(?<game>[a-z0-9]+)/mods/(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex NexusLink();

    [GeneratedRegex(@"^(?:https?://)?(?:www\.)?mod\.io/g/[^/]+/m/(?<name>[^/?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ModIoLink();
}
