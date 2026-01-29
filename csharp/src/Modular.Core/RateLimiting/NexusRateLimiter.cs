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
    private DateTimeOffset _hourlyReset = DateTimeOffset.UtcNow.AddHours(1).Date.AddHours(DateTimeOffset.UtcNow.Hour + 1);

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
            var now = DateTimeOffset.UtcNow;

            // Check if limits have reset
            if (now >= _dailyReset)
            {
                _dailyRemaining = _dailyLimit;
                _dailyReset = now.Date.AddDays(1);
            }

            if (now >= _hourlyReset)
            {
                _hourlyRemaining = _hourlyLimit;
                _hourlyReset = now.AddHours(1).Date.AddHours(now.Hour + 1);
            }

            return _dailyRemaining > 0 && _hourlyRemaining > 0;
        }
    }

    /// <inheritdoc />
    public async Task WaitIfNeededAsync(CancellationToken cancellationToken = default)
    {
        while (!CanMakeRequest())
        {
            TimeSpan waitTime;

            lock (_lock)
            {
                var now = DateTimeOffset.UtcNow;

                if (_dailyRemaining <= 0)
                {
                    waitTime = _dailyReset - now;
                    _logger?.LogWarning(
                        "Daily rate limit exhausted. Waiting until {ResetTime} ({WaitSeconds}s)",
                        _dailyReset, waitTime.TotalSeconds);
                }
                else if (_hourlyRemaining <= 0)
                {
                    waitTime = _hourlyReset - now;
                    _logger?.LogWarning(
                        "Hourly rate limit exhausted. Waiting until {ResetTime} ({WaitSeconds}s)",
                        _hourlyReset, waitTime.TotalSeconds);
                }
                else
                {
                    return; // Can make request
                }
            }

            if (waitTime > TimeSpan.Zero)
            {
                // Add a small buffer to ensure the limit has actually reset
                waitTime = waitTime.Add(TimeSpan.FromSeconds(1));
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
