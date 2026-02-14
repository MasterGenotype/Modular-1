using Microsoft.Extensions.Logging;

namespace Modular.Core.ErrorHandling;

/// <summary>
/// Retry policy with exponential backoff for handling transient failures.
/// </summary>
public class RetryPolicy
{
    private readonly int _maxAttempts;
    private readonly TimeSpan _initialDelay;
    private readonly double _backoffMultiplier;
    private readonly TimeSpan _maxDelay;
    private readonly ILogger? _logger;
    private readonly Func<Exception, bool>? _shouldRetry;

    public RetryPolicy(
        int maxAttempts = 3,
        TimeSpan? initialDelay = null,
        double backoffMultiplier = 2.0,
        TimeSpan? maxDelay = null,
        Func<Exception, bool>? shouldRetry = null,
        ILogger? logger = null)
    {
        _maxAttempts = maxAttempts;
        _initialDelay = initialDelay ?? TimeSpan.FromSeconds(1);
        _backoffMultiplier = backoffMultiplier;
        _maxDelay = maxDelay ?? TimeSpan.FromMinutes(1);
        _shouldRetry = shouldRetry ?? DefaultShouldRetry;
        _logger = logger;
    }

    /// <summary>
    /// Executes an action with retry logic.
    /// </summary>
    public RetryResult Execute(string operationName, Action action)
    {
        var result = new RetryResult
        {
            OperationName = operationName,
            StartedAt = DateTime.UtcNow
        };

        var currentDelay = _initialDelay;
        Exception? lastException = null;

        for (int attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            try
            {
                action();
                result.Success = true;
                result.AttemptCount = attempt;
                
                if (attempt > 1)
                {
                    _logger?.LogInformation(
                        "Operation {Operation} succeeded on attempt {Attempt}",
                        operationName, attempt);
                }

                return result;
            }
            catch (Exception ex)
            {
                lastException = ex;
                result.AttemptCount = attempt;

                if (_shouldRetry?.Invoke(ex) == false || attempt >= _maxAttempts)
                {
                    _logger?.LogError(
                        ex,
                        "Operation {Operation} failed after {Attempts} attempts (not retryable or max attempts reached)",
                        operationName, attempt);
                    break;
                }

                var delay = CalculateDelay(attempt, currentDelay);
                _logger?.LogWarning(
                    "Operation {Operation} failed on attempt {Attempt}/{MaxAttempts}, retrying in {Delay}ms: {Error}",
                    operationName, attempt, _maxAttempts, delay.TotalMilliseconds, ex.Message);

                Thread.Sleep(delay);
                currentDelay = TimeSpan.FromMilliseconds(currentDelay.TotalMilliseconds * _backoffMultiplier);
            }
        }

        result.Success = false;
        result.Exception = lastException;
        result.ErrorMessage = lastException?.Message;
        result.CompletedAt = DateTime.UtcNow;

        return result;
    }

    /// <summary>
    /// Executes an async action with retry logic.
    /// </summary>
    public async Task<RetryResult> ExecuteAsync(
        string operationName,
        Func<Task> action,
        CancellationToken ct = default)
    {
        var result = new RetryResult
        {
            OperationName = operationName,
            StartedAt = DateTime.UtcNow
        };

        var currentDelay = _initialDelay;
        Exception? lastException = null;

        for (int attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            try
            {
                await action();
                result.Success = true;
                result.AttemptCount = attempt;
                
                if (attempt > 1)
                {
                    _logger?.LogInformation(
                        "Operation {Operation} succeeded on attempt {Attempt}",
                        operationName, attempt);
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.Cancelled = true;
                result.ErrorMessage = "Operation was cancelled";
                result.AttemptCount = attempt;
                _logger?.LogInformation("Operation {Operation} was cancelled", operationName);
                break;
            }
            catch (Exception ex)
            {
                lastException = ex;
                result.AttemptCount = attempt;

                if (_shouldRetry?.Invoke(ex) == false || attempt >= _maxAttempts)
                {
                    _logger?.LogError(
                        ex,
                        "Operation {Operation} failed after {Attempts} attempts",
                        operationName, attempt);
                    break;
                }

                var delay = CalculateDelay(attempt, currentDelay);
                _logger?.LogWarning(
                    "Operation {Operation} failed on attempt {Attempt}/{MaxAttempts}, retrying in {Delay}ms: {Error}",
                    operationName, attempt, _maxAttempts, delay.TotalMilliseconds, ex.Message);

                await Task.Delay(delay, ct);
                currentDelay = TimeSpan.FromMilliseconds(currentDelay.TotalMilliseconds * _backoffMultiplier);
            }
        }

        result.Success = false;
        result.Exception = lastException;
        result.ErrorMessage = lastException?.Message;
        result.CompletedAt = DateTime.UtcNow;

        return result;
    }

    /// <summary>
    /// Executes a function with retry logic.
    /// </summary>
    public async Task<RetryResult<T>> ExecuteAsync<T>(
        string operationName,
        Func<Task<T>> action,
        CancellationToken ct = default)
    {
        var result = new RetryResult<T>
        {
            OperationName = operationName,
            StartedAt = DateTime.UtcNow
        };

        var currentDelay = _initialDelay;
        Exception? lastException = null;

        for (int attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            try
            {
                result.Value = await action();
                result.Success = true;
                result.AttemptCount = attempt;
                
                if (attempt > 1)
                {
                    _logger?.LogInformation(
                        "Operation {Operation} succeeded on attempt {Attempt}",
                        operationName, attempt);
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.Cancelled = true;
                result.ErrorMessage = "Operation was cancelled";
                result.AttemptCount = attempt;
                _logger?.LogInformation("Operation {Operation} was cancelled", operationName);
                break;
            }
            catch (Exception ex)
            {
                lastException = ex;
                result.AttemptCount = attempt;

                if (_shouldRetry?.Invoke(ex) == false || attempt >= _maxAttempts)
                {
                    _logger?.LogError(
                        ex,
                        "Operation {Operation} failed after {Attempts} attempts",
                        operationName, attempt);
                    break;
                }

                var delay = CalculateDelay(attempt, currentDelay);
                _logger?.LogWarning(
                    "Operation {Operation} failed on attempt {Attempt}/{MaxAttempts}, retrying in {Delay}ms: {Error}",
                    operationName, attempt, _maxAttempts, delay.TotalMilliseconds, ex.Message);

                await Task.Delay(delay, ct);
                currentDelay = TimeSpan.FromMilliseconds(currentDelay.TotalMilliseconds * _backoffMultiplier);
            }
        }

        result.Success = false;
        result.Exception = lastException;
        result.ErrorMessage = lastException?.Message;
        result.CompletedAt = DateTime.UtcNow;

        return result;
    }

    private TimeSpan CalculateDelay(int attempt, TimeSpan currentDelay)
    {
        // Add jitter to prevent thundering herd
        var jitter = Random.Shared.NextDouble() * 0.1 * currentDelay.TotalMilliseconds;
        var delayWithJitter = TimeSpan.FromMilliseconds(currentDelay.TotalMilliseconds + jitter);
        
        return delayWithJitter > _maxDelay ? _maxDelay : delayWithJitter;
    }

    private static bool DefaultShouldRetry(Exception ex)
    {
        // Retry on network-related exceptions and transient failures
        return ex is HttpRequestException
            || ex is TimeoutException
            || ex is IOException
            || (ex is TaskCanceledException && ex.InnerException is TimeoutException);
    }

    /// <summary>
    /// Default retry policy with 3 attempts and exponential backoff.
    /// </summary>
    public static RetryPolicy Default => new(
        maxAttempts: 3,
        initialDelay: TimeSpan.FromSeconds(1),
        backoffMultiplier: 2.0,
        maxDelay: TimeSpan.FromMinutes(1));

    /// <summary>
    /// Aggressive retry policy with more attempts.
    /// </summary>
    public static RetryPolicy Aggressive => new(
        maxAttempts: 5,
        initialDelay: TimeSpan.FromMilliseconds(500),
        backoffMultiplier: 2.0,
        maxDelay: TimeSpan.FromMinutes(2));
}

/// <summary>
/// Result of a retry operation.
/// </summary>
public class RetryResult
{
    public bool Success { get; set; }
    public string OperationName { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public string? ErrorMessage { get; set; }
    public Exception? Exception { get; set; }
    public bool Cancelled { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
    public TimeSpan Duration => CompletedAt - StartedAt;
}

/// <summary>
/// Result of a retry operation with a return value.
/// </summary>
public class RetryResult<T> : RetryResult
{
    public T? Value { get; set; }
}
