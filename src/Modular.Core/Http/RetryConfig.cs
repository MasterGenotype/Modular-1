namespace Modular.Core.Http;

/// <summary>
/// Configuration for HTTP request retry behavior.
/// </summary>
public class RetryConfig
{
    /// <summary>
    /// Maximum number of retry attempts.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Initial delay before first retry in milliseconds.
    /// </summary>
    public int InitialDelayMs { get; set; } = 1000;

    /// <summary>
    /// Maximum delay between retries in milliseconds.
    /// </summary>
    public int MaxDelayMs { get; set; } = 16000;

    /// <summary>
    /// Whether to use exponential backoff for retry delays.
    /// </summary>
    public bool ExponentialBackoff { get; set; } = true;

    /// <summary>
    /// Gets the delay for a specific retry attempt.
    /// </summary>
    /// <param name="attempt">The retry attempt number (0-based)</param>
    /// <returns>Delay in milliseconds</returns>
    public int GetDelay(int attempt)
    {
        if (!ExponentialBackoff)
            return InitialDelayMs;

        var delay = InitialDelayMs * (int)Math.Pow(2, attempt);
        return Math.Min(delay, MaxDelayMs);
    }

    /// <summary>
    /// Determines if a status code should trigger a retry.
    /// Retries on 5xx errors and 429 (rate limit), but not other 4xx errors.
    /// </summary>
    /// <param name="statusCode">HTTP status code</param>
    /// <returns>True if should retry</returns>
    public static bool ShouldRetry(int statusCode)
    {
        return statusCode >= 500 || statusCode == 429;
    }
}
