using Microsoft.Extensions.Logging;

namespace Modular.Core.RateLimiting;

/// <summary>
/// Multi-backend rate limit scheduler with budget tracking and prioritization.
/// Coordinates rate limits across multiple backends.
/// </summary>
public class RateLimitScheduler : IRateLimitScheduler
{
    private readonly Dictionary<string, BackendBudget> _budgets = new();
    private readonly Dictionary<string, CircuitBreaker> _circuitBreakers = new();
    private readonly object _lock = new();
    private readonly ILogger<RateLimitScheduler>? _logger;

    public RateLimitScheduler(ILogger<RateLimitScheduler>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Registers a backend with its rate limits.
    /// </summary>
    public void RegisterBackend(string backendId, RateLimitConfig config)
    {
        lock (_lock)
        {
            _budgets[backendId] = new BackendBudget(config);
            _circuitBreakers[backendId] = new CircuitBreaker(config.CircuitBreakerThreshold);
            _logger?.LogInformation("Registered backend {BackendId} with limits: {Daily} daily, {Hourly} hourly, {Concurrent} concurrent",
                backendId, config.DailyLimit, config.HourlyLimit, config.ConcurrentLimit);
        }
    }

    /// <summary>
    /// Waits for a request slot to become available, respecting priority.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(
        string backendId,
        RequestPriority priority = RequestPriority.Normal,
        CancellationToken ct = default)
    {
        BackendBudget? budget;
        CircuitBreaker? circuitBreaker;

        lock (_lock)
        {
            if (!_budgets.TryGetValue(backendId, out budget))
                throw new InvalidOperationException($"Backend {backendId} not registered");

            if (!_circuitBreakers.TryGetValue(backendId, out circuitBreaker))
                throw new InvalidOperationException($"Circuit breaker for {backendId} not found");
        }

        // Check circuit breaker
        if (circuitBreaker.IsOpen)
        {
            var waitTime = circuitBreaker.GetWaitTime();
            _logger?.LogWarning("Circuit breaker open for {BackendId}, waiting {Seconds}s", 
                backendId, waitTime.TotalSeconds);
            await Task.Delay(waitTime, ct);
            circuitBreaker.AttemptReset();
        }

        // Wait for budget availability
        while (!budget.CanMakeRequest())
        {
            ct.ThrowIfCancellationRequested();
            
            var delay = GetDelayForPriority(priority);
            await Task.Delay(delay, ct);
            budget.Refresh();
        }

        // Acquire concurrent slot
        await budget.AcquireConcurrentSlotAsync(ct);

        // Reserve the request
        budget.ReserveRequest();

        return new RateLimitHandle(budget, circuitBreaker, backendId, _logger);
    }

    /// <summary>
    /// Updates rate limits from response headers.
    /// </summary>
    public void UpdateFromHeaders(string backendId, IDictionary<string, string> headers)
    {
        lock (_lock)
        {
            if (!_budgets.TryGetValue(backendId, out var budget))
                return;

            budget.UpdateFromHeaders(headers);
        }
    }

    /// <summary>
    /// Records a request failure for circuit breaker.
    /// </summary>
    public void RecordFailure(string backendId)
    {
        lock (_lock)
        {
            if (_circuitBreakers.TryGetValue(backendId, out var breaker))
            {
                breaker.RecordFailure();
                if (breaker.IsOpen)
                    _logger?.LogWarning("Circuit breaker opened for {BackendId} after failures", backendId);
            }
        }
    }

    /// <summary>
    /// Records a successful request for circuit breaker.
    /// </summary>
    public void RecordSuccess(string backendId)
    {
        lock (_lock)
        {
            if (_circuitBreakers.TryGetValue(backendId, out var breaker))
                breaker.RecordSuccess();
        }
    }

    /// <summary>
    /// Gets current budget status for a backend.
    /// </summary>
    public BudgetStatus GetStatus(string backendId)
    {
        lock (_lock)
        {
            if (!_budgets.TryGetValue(backendId, out var budget))
                throw new InvalidOperationException($"Backend {backendId} not registered");

            return new BudgetStatus
            {
                DailyRemaining = budget.DailyRemaining,
                HourlyRemaining = budget.HourlyRemaining,
                ConcurrentActive = budget.ConcurrentActive,
                DailyLimit = budget.Config.DailyLimit,
                HourlyLimit = budget.Config.HourlyLimit,
                ConcurrentLimit = budget.Config.ConcurrentLimit,
                DailyResetAt = budget.DailyResetAt,
                HourlyResetAt = budget.HourlyResetAt
            };
        }
    }

    private TimeSpan GetDelayForPriority(RequestPriority priority)
    {
        return priority switch
        {
            RequestPriority.Critical => TimeSpan.FromMilliseconds(100),
            RequestPriority.High => TimeSpan.FromMilliseconds(250),
            RequestPriority.Normal => TimeSpan.FromMilliseconds(500),
            RequestPriority.Low => TimeSpan.FromSeconds(1),
            RequestPriority.Background => TimeSpan.FromSeconds(2),
            _ => TimeSpan.FromMilliseconds(500)
        };
    }
}

/// <summary>
/// Tracks rate limit budget for a backend.
/// </summary>
internal class BackendBudget
{
    public RateLimitConfig Config { get; }
    public int DailyRemaining { get; private set; }
    public int HourlyRemaining { get; private set; }
    public int ConcurrentActive { get; private set; }
    public DateTime DailyResetAt { get; private set; }
    public DateTime HourlyResetAt { get; private set; }

    private readonly SemaphoreSlim _concurrentSemaphore;
    private readonly object _lock = new();

    public BackendBudget(RateLimitConfig config)
    {
        Config = config;
        DailyRemaining = config.DailyLimit;
        HourlyRemaining = config.HourlyLimit;
        DailyResetAt = DateTime.UtcNow.AddDays(1).Date;
        HourlyResetAt = DateTime.UtcNow.AddHours(1);
        _concurrentSemaphore = new SemaphoreSlim(config.ConcurrentLimit, config.ConcurrentLimit);
    }

    public bool CanMakeRequest()
    {
        lock (_lock)
        {
            Refresh();
            return DailyRemaining > 0 && HourlyRemaining > 0;
        }
    }

    public void ReserveRequest()
    {
        lock (_lock)
        {
            DailyRemaining--;
            HourlyRemaining--;
        }
    }

    public async Task AcquireConcurrentSlotAsync(CancellationToken ct)
    {
        await _concurrentSemaphore.WaitAsync(ct);
        lock (_lock)
        {
            ConcurrentActive++;
        }
    }

    public void ReleaseConcurrentSlot()
    {
        lock (_lock)
        {
            ConcurrentActive--;
        }
        _concurrentSemaphore.Release();
    }

    public void Refresh()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;

            // Reset daily budget
            if (now >= DailyResetAt)
            {
                DailyRemaining = Config.DailyLimit;
                DailyResetAt = now.AddDays(1).Date;
            }

            // Reset hourly budget
            if (now >= HourlyResetAt)
            {
                HourlyRemaining = Config.HourlyLimit;
                HourlyResetAt = now.AddHours(1);
            }
        }
    }

    public void UpdateFromHeaders(IDictionary<string, string> headers)
    {
        lock (_lock)
        {
            // NexusMods-style headers
            if (headers.TryGetValue("X-RL-Daily-Remaining", out var dailyStr) && 
                int.TryParse(dailyStr, out var daily))
                DailyRemaining = daily;

            if (headers.TryGetValue("X-RL-Hourly-Remaining", out var hourlyStr) && 
                int.TryParse(hourlyStr, out var hourly))
                HourlyRemaining = hourly;

            if (headers.TryGetValue("X-RL-Daily-Reset", out var dailyResetStr) && 
                long.TryParse(dailyResetStr, out var dailyResetTs))
                DailyResetAt = DateTimeOffset.FromUnixTimeSeconds(dailyResetTs).DateTime;

            if (headers.TryGetValue("X-RL-Hourly-Reset", out var hourlyResetStr) && 
                long.TryParse(hourlyResetStr, out var hourlyResetTs))
                HourlyResetAt = DateTimeOffset.FromUnixTimeSeconds(hourlyResetTs).DateTime;
        }
    }
}

/// <summary>
/// Circuit breaker for failing backends.
/// </summary>
internal class CircuitBreaker
{
    private readonly int _threshold;
    private int _failureCount;
    private DateTime? _openedAt;
    private readonly TimeSpan _resetTimeout = TimeSpan.FromMinutes(5);

    public bool IsOpen => _openedAt.HasValue && 
                          DateTime.UtcNow < _openedAt.Value + _resetTimeout;

    public CircuitBreaker(int threshold)
    {
        _threshold = threshold;
    }

    public void RecordFailure()
    {
        _failureCount++;
        if (_failureCount >= _threshold && !_openedAt.HasValue)
            _openedAt = DateTime.UtcNow;
    }

    public void RecordSuccess()
    {
        _failureCount = 0;
        _openedAt = null;
    }

    public void AttemptReset()
    {
        if (_openedAt.HasValue && DateTime.UtcNow >= _openedAt.Value + _resetTimeout)
        {
            _openedAt = null;
            _failureCount = 0;
        }
    }

    public TimeSpan GetWaitTime()
    {
        if (!_openedAt.HasValue) return TimeSpan.Zero;
        var elapsed = DateTime.UtcNow - _openedAt.Value;
        var remaining = _resetTimeout - elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}

/// <summary>
/// Handle for rate-limited requests.
/// </summary>
internal class RateLimitHandle : IDisposable
{
    private readonly BackendBudget _budget;
    private readonly CircuitBreaker _circuitBreaker;
    private readonly string _backendId;
    private readonly ILogger? _logger;
    private bool _disposed;

    public RateLimitHandle(
        BackendBudget budget, 
        CircuitBreaker circuitBreaker,
        string backendId,
        ILogger? logger)
    {
        _budget = budget;
        _circuitBreaker = circuitBreaker;
        _backendId = backendId;
        _logger = logger;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _budget.ReleaseConcurrentSlot();
        _circuitBreaker.RecordSuccess();
        _logger?.LogDebug("Released rate limit slot for {BackendId}", _backendId);
    }
}

/// <summary>
/// Interface for rate limit scheduling.
/// </summary>
public interface IRateLimitScheduler
{
    void RegisterBackend(string backendId, RateLimitConfig config);
    Task<IDisposable> AcquireAsync(string backendId, RequestPriority priority = RequestPriority.Normal, CancellationToken ct = default);
    void UpdateFromHeaders(string backendId, IDictionary<string, string> headers);
    void RecordFailure(string backendId);
    void RecordSuccess(string backendId);
    BudgetStatus GetStatus(string backendId);
}

/// <summary>
/// Rate limit configuration for a backend.
/// </summary>
public class RateLimitConfig
{
    public int DailyLimit { get; set; } = int.MaxValue;
    public int HourlyLimit { get; set; } = int.MaxValue;
    public int ConcurrentLimit { get; set; } = 5;
    public int CircuitBreakerThreshold { get; set; } = 5;
}

/// <summary>
/// Request priority for scheduling.
/// </summary>
public enum RequestPriority
{
    Critical = 0,   // User-initiated, blocking UI
    High = 1,       // User-initiated, non-blocking
    Normal = 2,     // Standard operations
    Low = 3,        // Background sync
    Background = 4  // Prefetch, thumbnails
}

/// <summary>
/// Current budget status for a backend.
/// </summary>
public class BudgetStatus
{
    public int DailyRemaining { get; set; }
    public int HourlyRemaining { get; set; }
    public int ConcurrentActive { get; set; }
    public int DailyLimit { get; set; }
    public int HourlyLimit { get; set; }
    public int ConcurrentLimit { get; set; }
    public DateTime DailyResetAt { get; set; }
    public DateTime HourlyResetAt { get; set; }

    public double DailyPercentageUsed => DailyLimit > 0 ? (1.0 - (double)DailyRemaining / DailyLimit) * 100 : 0;
    public double HourlyPercentageUsed => HourlyLimit > 0 ? (1.0 - (double)HourlyRemaining / HourlyLimit) * 100 : 0;
}
