namespace Modular.Core.RateLimiting;

/// <summary>
/// Interface for rate limiting HTTP requests.
/// </summary>
public interface IRateLimiter
{
    /// <summary>
    /// Update rate limit state from API response headers.
    /// </summary>
    /// <param name="headers">Response headers</param>
    void UpdateFromHeaders(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers);

    /// <summary>
    /// Check if we can make a request without waiting.
    /// </summary>
    /// <returns>True if request is allowed</returns>
    bool CanMakeRequest();

    /// <summary>
    /// Wait until rate limits allow a request.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task WaitIfNeededAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reserves a request slot by decrementing remaining counts.
    /// Call BEFORE making a request to prevent exceeding limits.
    /// </summary>
    void ReserveRequest();

    /// <summary>
    /// Save rate limit state to file.
    /// </summary>
    /// <param name="path">Path to state file</param>
    Task SaveStateAsync(string path);

    /// <summary>
    /// Load rate limit state from file.
    /// </summary>
    /// <param name="path">Path to state file</param>
    Task LoadStateAsync(string path);

    /// <summary>
    /// Gets the remaining daily requests.
    /// </summary>
    int DailyRemaining { get; }

    /// <summary>
    /// Gets the remaining hourly requests.
    /// </summary>
    int HourlyRemaining { get; }

    /// <summary>
    /// Gets the daily request limit.
    /// </summary>
    int DailyLimit { get; }

    /// <summary>
    /// Gets the hourly request limit.
    /// </summary>
    int HourlyLimit { get; }

    /// <summary>
    /// Gets the time when the daily limit resets.
    /// </summary>
    DateTimeOffset DailyReset { get; }

    /// <summary>
    /// Gets the time when the hourly limit resets.
    /// </summary>
    DateTimeOffset HourlyReset { get; }
}
