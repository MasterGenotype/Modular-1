using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Modular.Core.RateLimiting;

/// <summary>
/// Rate limiter for NexusMods API.
/// 
/// NexusMods enforces:
/// - 20,000 requests per 24-hour period (resets at 00:00 GMT)
/// - 500 requests per hour after daily limit reached (resets on the hour)
/// 
/// Thread-safe for concurrent access.
/// </summary>
public class NexusRateLimiter : IRateLimiter
{
    private readonly ILogger<NexusRateLimiter>? _logger;
    private readonly object _lock = new();

    // Rate limit state
    private int _dailyLimit = 20000;
    private int _dailyRemaining = 20000;
    private int _hourlyLimit = 500;
    private int _hourlyRemaining = 500;
    private DateTimeOffset _dailyReset = DateTimeOffset.UtcNow.Date.AddDays(1);
    private DateTimeOffset _hourlyReset = new DateTimeOffset(DateTimeOffset.UtcNow.Year, DateTimeOffset.UtcNow.Month, DateTimeOffset.UtcNow.Day, DateTimeOffset.UtcNow.Hour, 0, 0, TimeSpan.Zero).AddHours(1);

    public NexusRateLimiter(ILogger<NexusRateLimiter>? logger = null)
    {
        _logger = logger;
    }

    public int DailyRemaining
    {
        get { lock (_lock) return _dailyRemaining; }
    }

    public int HourlyRemaining
    {
        get { lock (_lock) return _hourlyRemaining; }
    }

    public int DailyLimit
    {
        get { lock (_lock) return _dailyLimit; }
    }

    public int HourlyLimit
    {
        get { lock (_lock) return _hourlyLimit; }
    }

    public DateTimeOffset DailyReset
    {
        get { lock (_lock) return _dailyReset; }
    }

    public DateTimeOffset HourlyReset
    {
        get { lock (_lock) return _hourlyReset; }
    }

    /// <inheritdoc />
    public void UpdateFromHeaders(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
    {
        var headerDict = headers
            .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Value.FirstOrDefault(), StringComparer.OrdinalIgnoreCase);

        lock (_lock)
        {
            // Parse daily limits
            if (headerDict.TryGetValue("x-rl-daily-limit", out var dailyLimitStr) &&
                int.TryParse(dailyLimitStr, out var dailyLimit))
            {
                _dailyLimit = dailyLimit;
            }

            if (headerDict.TryGetValue("x-rl-daily-remaining", out var dailyRemainingStr) &&
                int.TryParse(dailyRemainingStr, out var dailyRemaining))
            {
                _dailyRemaining = dailyRemaining;
            }

            if (headerDict.TryGetValue("x-rl-daily-reset", out var dailyResetStr) &&
                long.TryParse(dailyResetStr, out var dailyResetUnix))
            {
                _dailyReset = DateTimeOffset.FromUnixTimeSeconds(dailyResetUnix);
            }

            // Parse hourly limits
            if (headerDict.TryGetValue("x-rl-hourly-limit", out var hourlyLimitStr) &&
                int.TryParse(hourlyLimitStr, out var hourlyLimit))
            {
                _hourlyLimit = hourlyLimit;
            }

            if (headerDict.TryGetValue("x-rl-hourly-remaining", out var hourlyRemainingStr) &&
                int.TryParse(hourlyRemainingStr, out var hourlyRemaining))
            {
                _hourlyRemaining = hourlyRemaining;
            }

            if (headerDict.TryGetValue("x-rl-hourly-reset", out var hourlyResetStr) &&
                long.TryParse(hourlyResetStr, out var hourlyResetUnix))
            {
                _hourlyReset = DateTimeOffset.FromUnixTimeSeconds(hourlyResetUnix);
            }

            _logger?.LogDebug(
                "Rate limits updated: Daily {DailyRemaining}/{DailyLimit} (reset {DailyReset}), " +
                "Hourly {HourlyRemaining}/{HourlyLimit} (reset {HourlyReset})",
                _dailyRemaining, _dailyLimit, _dailyReset,
                _hourlyRemaining, _hourlyLimit, _hourlyReset);
        }
    }

    /// <inheritdoc />
    public bool CanMakeRequest()
    {
        lock (_lock)
        {
            CheckAndResetLimits();
            return _dailyRemaining > 0 && _hourlyRemaining > 0;
        }
    }

    /// <summary>
    /// Reserves a request slot by decrementing the remaining counts.
    /// Call this BEFORE making a request to prevent exceeding limits.
    /// The actual counts will be corrected when UpdateFromHeaders is called.
    /// </summary>
    public void ReserveRequest()
    {
        lock (_lock)
        {
            CheckAndResetLimits();
            
            if (_dailyRemaining > 0)
                _dailyRemaining--;
            if (_hourlyRemaining > 0)
                _hourlyRemaining--;
                
            _logger?.LogDebug("Reserved request slot. Remaining: Daily={Daily}, Hourly={Hourly}",
                _dailyRemaining, _hourlyRemaining);
        }
    }

    /// <summary>
    /// Checks if limits have reset and updates the remaining counts accordingly.
    /// Must be called while holding _lock.
    /// </summary>
    private void CheckAndResetLimits()
    {
        var now = DateTimeOffset.UtcNow;

        // Check if limits have reset
        if (now >= _dailyReset)
        {
            _dailyRemaining = _dailyLimit;
            _dailyReset = now.Date.AddDays(1);
            _logger?.LogDebug("Daily limit reset to {Limit}", _dailyLimit);
        }

        if (now >= _hourlyReset)
        {
            _hourlyRemaining = _hourlyLimit;
            // Calculate next hour boundary in UTC to avoid timezone conversion issues
            _hourlyReset = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero).AddHours(1);
            _logger?.LogDebug("Hourly limit reset to {Limit}", _hourlyLimit);
        }
    }

    /// <summary>
    /// Maximum time to wait for daily rate limit reset. Daily limits are too long to wait for.
    /// </summary>
    private static readonly TimeSpan MaxDailyWaitTime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum time to wait for hourly rate limit reset. 
    /// NexusMods hourly limits reset at the top of each hour (max 60 min wait).
    /// </summary>
    private static readonly TimeSpan MaxHourlyWaitTime = TimeSpan.FromMinutes(61);

    /// <inheritdoc />
    public async Task WaitIfNeededAsync(CancellationToken cancellationToken = default)
    {
        _logger?.LogDebug("[RateLimiter] Checking: Daily={DailyRemaining}/{DailyLimit}, Hourly={HourlyRemaining}/{HourlyLimit}",
            _dailyRemaining, _dailyLimit, _hourlyRemaining, _hourlyLimit);
        while (!CanMakeRequest())
        {
            TimeSpan waitTime;
            bool isHourlyLimit = false;

            lock (_lock)
            {
                var now = DateTimeOffset.UtcNow;

                if (_dailyRemaining <= 0)
                {
                    waitTime = _dailyReset - now;
                    _logger?.LogWarning(
                        "Daily rate limit exhausted. Would need to wait until {ResetTime} ({WaitSeconds}s)",
                        _dailyReset, waitTime.TotalSeconds);
                }
                else if (_hourlyRemaining <= 0)
                {
                    isHourlyLimit = true;
                    waitTime = _hourlyReset - now;
                    
                    // Cap hourly wait to max 60 minutes - NexusMods resets at top of each hour
                    // If stored reset time seems wrong, calculate time until next hour boundary
                    if (waitTime > MaxHourlyWaitTime || waitTime < TimeSpan.Zero)
                    {
                        // Calculate next hour boundary in UTC (now is DateTimeOffset.UtcNow)
                        // Using DateTimeOffset consistently to avoid timezone conversion issues
                        var nextHour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero).AddHours(1);
                        waitTime = nextHour - now;
                        _hourlyReset = nextHour;
                        _logger?.LogDebug("Corrected hourly reset time to {ResetTime}", nextHour);
                    }
                    
                    _logger?.LogWarning(
                        "Hourly rate limit exhausted. Waiting until {ResetTime} ({WaitMinutes:F1} minutes)",
                        _hourlyReset, waitTime.TotalMinutes);
                }
                else
                {
                    return; // Can make request
                }
            }

            // For daily limits, don't wait (too long). For hourly limits, wait up to 60 min.
            var maxWait = isHourlyLimit ? MaxHourlyWaitTime : MaxDailyWaitTime;
            if (waitTime > maxWait)
            {
                var limitType = isHourlyLimit ? "Hourly" : "Daily";
                _logger?.LogError("{LimitType} rate limit wait time ({WaitTime}) exceeds maximum ({MaxWait}). Aborting.",
                    limitType, waitTime, maxWait);
                throw new InvalidOperationException(
                    $"NexusMods {limitType.ToLower()} rate limit exceeded. Would need to wait {waitTime.TotalMinutes:F1} minutes. " +
                    $"Maximum wait is {maxWait.TotalMinutes:F0} minutes. Try again later.");
            }

            if (waitTime > TimeSpan.Zero)
            {
                _logger?.LogInformation("[RateLimiter] Waiting {WaitMinutes:F1} minutes for hourly rate limit reset...", waitTime.TotalMinutes);
                // Add a small buffer to ensure the limit has actually reset
                waitTime = waitTime.Add(TimeSpan.FromSeconds(2));
                await Task.Delay(waitTime, cancellationToken);
            }
        }
    }

    /// <inheritdoc />
    public async Task SaveStateAsync(string path)
    {
        RateLimitState state;
        lock (_lock)
        {
            state = new RateLimitState
            {
                DailyLimit = _dailyLimit,
                DailyRemaining = _dailyRemaining,
                DailyReset = _dailyReset.ToUnixTimeSeconds(),
                HourlyLimit = _hourlyLimit,
                HourlyRemaining = _hourlyRemaining,
                HourlyReset = _hourlyReset.ToUnixTimeSeconds()
            };
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);

        _logger?.LogDebug("Rate limit state saved to {Path}", path);
    }

    /// <inheritdoc />
    public async Task LoadStateAsync(string path)
    {
        if (!File.Exists(path))
        {
            _logger?.LogDebug("No rate limit state file found at {Path}", path);
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path);
            var state = JsonSerializer.Deserialize<RateLimitState>(json);

            if (state != null)
            {
                lock (_lock)
                {
                    _dailyLimit = state.DailyLimit;
                    _dailyRemaining = state.DailyRemaining;
                    _dailyReset = DateTimeOffset.FromUnixTimeSeconds(state.DailyReset);
                    _hourlyLimit = state.HourlyLimit;
                    _hourlyRemaining = state.HourlyRemaining;
                    _hourlyReset = DateTimeOffset.FromUnixTimeSeconds(state.HourlyReset);
                }

                _logger?.LogDebug(
                    "Rate limit state loaded: Daily {DailyRemaining}/{DailyLimit}, Hourly {HourlyRemaining}/{HourlyLimit}",
                    _dailyRemaining, _dailyLimit, _hourlyRemaining, _hourlyLimit);
            }
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Failed to parse rate limit state file, using defaults");
        }
    }

    private class RateLimitState
    {
        [JsonPropertyName("daily_limit")]
        public int DailyLimit { get; set; }

        [JsonPropertyName("daily_remaining")]
        public int DailyRemaining { get; set; }

        [JsonPropertyName("daily_reset")]
        public long DailyReset { get; set; }

        [JsonPropertyName("hourly_limit")]
        public int HourlyLimit { get; set; }

        [JsonPropertyName("hourly_remaining")]
        public int HourlyRemaining { get; set; }

        [JsonPropertyName("hourly_reset")]
        public long HourlyReset { get; set; }
    }
}
