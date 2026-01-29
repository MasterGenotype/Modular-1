namespace Modular.FluentHttp.Interfaces;

/// <summary>
/// Configuration for request retry behavior.
/// </summary>
public interface IRetryConfig
{
    /// <summary>
    /// Maximum number of retry attempts.
    /// </summary>
    int MaxRetries { get; }

    /// <summary>
    /// Determines if a request should be retried based on status code and timeout.
    /// </summary>
    bool ShouldRetry(int statusCode, bool isTimeout);

    /// <summary>
    /// Gets the delay before the next retry attempt.
    /// </summary>
    TimeSpan GetDelay(int attempt);
}

/// <summary>
/// Default retry configuration with exponential backoff.
/// </summary>
public class DefaultRetryConfig : IRetryConfig
{
    public int MaxRetries { get; set; } = 3;
    public int InitialDelayMs { get; set; } = 1000;
    public int MaxDelayMs { get; set; } = 16000;
    public bool ExponentialBackoff { get; set; } = true;

    public bool ShouldRetry(int statusCode, bool isTimeout)
    {
        return isTimeout || statusCode >= 500 || statusCode == 429;
    }

    public TimeSpan GetDelay(int attempt)
    {
        if (!ExponentialBackoff)
            return TimeSpan.FromMilliseconds(InitialDelayMs);

        var delay = InitialDelayMs * (int)Math.Pow(2, attempt);
        return TimeSpan.FromMilliseconds(Math.Min(delay, MaxDelayMs));
    }
}
