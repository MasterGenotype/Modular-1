using Microsoft.Extensions.Logging;

namespace Modular.Core.ErrorHandling;

/// <summary>
/// Provides error boundaries for isolated execution with fallback and recovery.
/// </summary>
public class ErrorBoundary
{
    private readonly ILogger? _logger;
    private readonly ErrorBoundaryPolicy _policy;

    public ErrorBoundary(ErrorBoundaryPolicy policy, ILogger? logger = null)
    {
        _policy = policy;
        _logger = logger;
    }

    /// <summary>
    /// Executes an action within an error boundary.
    /// </summary>
    public ErrorBoundaryResult Execute(string operationName, Action action)
    {
        var result = new ErrorBoundaryResult
        {
            OperationName = operationName,
            StartedAt = DateTime.UtcNow
        };

        try
        {
            action();
            result.Success = true;
            _logger?.LogDebug("Operation {Operation} completed successfully", operationName);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Exception = ex;
            result.ErrorMessage = ex.Message;

            HandleError(operationName, ex);
        }
        finally
        {
            result.CompletedAt = DateTime.UtcNow;
        }

        return result;
    }

    /// <summary>
    /// Executes an async action within an error boundary.
    /// </summary>
    public async Task<ErrorBoundaryResult> ExecuteAsync(
        string operationName, 
        Func<Task> action,
        CancellationToken ct = default)
    {
        var result = new ErrorBoundaryResult
        {
            OperationName = operationName,
            StartedAt = DateTime.UtcNow
        };

        try
        {
            await action();
            result.Success = true;
            _logger?.LogDebug("Async operation {Operation} completed successfully", operationName);
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Cancelled = true;
            result.ErrorMessage = "Operation was cancelled";
            _logger?.LogInformation("Operation {Operation} was cancelled", operationName);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Exception = ex;
            result.ErrorMessage = ex.Message;

            HandleError(operationName, ex);
        }
        finally
        {
            result.CompletedAt = DateTime.UtcNow;
        }

        return result;
    }

    /// <summary>
    /// Executes a function within an error boundary with a fallback value.
    /// </summary>
    public ErrorBoundaryResult<T> Execute<T>(
        string operationName,
        Func<T> action,
        T fallbackValue)
    {
        var result = new ErrorBoundaryResult<T>
        {
            OperationName = operationName,
            StartedAt = DateTime.UtcNow,
            Value = fallbackValue
        };

        try
        {
            result.Value = action();
            result.Success = true;
            _logger?.LogDebug("Operation {Operation} completed successfully", operationName);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Exception = ex;
            result.ErrorMessage = ex.Message;
            result.UsedFallback = true;

            HandleError(operationName, ex);

            if (_policy.ReturnFallbackOnError)
            {
                _logger?.LogWarning("Operation {Operation} failed, using fallback value", operationName);
            }
        }
        finally
        {
            result.CompletedAt = DateTime.UtcNow;
        }

        return result;
    }

    /// <summary>
    /// Executes an async function within an error boundary with a fallback value.
    /// </summary>
    public async Task<ErrorBoundaryResult<T>> ExecuteAsync<T>(
        string operationName,
        Func<Task<T>> action,
        T fallbackValue,
        CancellationToken ct = default)
    {
        var result = new ErrorBoundaryResult<T>
        {
            OperationName = operationName,
            StartedAt = DateTime.UtcNow,
            Value = fallbackValue
        };

        try
        {
            result.Value = await action();
            result.Success = true;
            _logger?.LogDebug("Async operation {Operation} completed successfully", operationName);
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Cancelled = true;
            result.ErrorMessage = "Operation was cancelled";
            result.UsedFallback = true;
            _logger?.LogInformation("Operation {Operation} was cancelled, using fallback", operationName);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Exception = ex;
            result.ErrorMessage = ex.Message;
            result.UsedFallback = true;

            HandleError(operationName, ex);

            if (_policy.ReturnFallbackOnError)
            {
                _logger?.LogWarning("Operation {Operation} failed, using fallback value", operationName);
            }
        }
        finally
        {
            result.CompletedAt = DateTime.UtcNow;
        }

        return result;
    }

    private void HandleError(string operationName, Exception ex)
    {
        var severity = ClassifyError(ex);

        switch (severity)
        {
            case ErrorSeverity.Critical:
                _logger?.LogCritical(ex, "Critical error in {Operation}", operationName);
                if (_policy.ThrowOnCriticalError)
                    throw new ErrorBoundaryException($"Critical error in {operationName}", ex);
                break;

            case ErrorSeverity.Error:
                _logger?.LogError(ex, "Error in {Operation}", operationName);
                break;

            case ErrorSeverity.Warning:
                _logger?.LogWarning(ex, "Warning in {Operation}", operationName);
                break;
        }
    }

    private ErrorSeverity ClassifyError(Exception ex)
    {
        return ex switch
        {
            OutOfMemoryException => ErrorSeverity.Critical,
            StackOverflowException => ErrorSeverity.Critical,
            AccessViolationException => ErrorSeverity.Critical,
            FileNotFoundException => ErrorSeverity.Warning,
            DirectoryNotFoundException => ErrorSeverity.Warning,
            _ => ErrorSeverity.Error
        };
    }
}

/// <summary>
/// Policy for error boundary behavior.
/// </summary>
public class ErrorBoundaryPolicy
{
    /// <summary>
    /// Whether to return fallback values on error.
    /// </summary>
    public bool ReturnFallbackOnError { get; set; } = true;

    /// <summary>
    /// Whether to throw on critical errors.
    /// </summary>
    public bool ThrowOnCriticalError { get; set; } = false;

    /// <summary>
    /// Default permissive policy.
    /// </summary>
    public static ErrorBoundaryPolicy Permissive => new()
    {
        ReturnFallbackOnError = true,
        ThrowOnCriticalError = false
    };

    /// <summary>
    /// Strict policy that throws on critical errors.
    /// </summary>
    public static ErrorBoundaryPolicy Strict => new()
    {
        ReturnFallbackOnError = false,
        ThrowOnCriticalError = true
    };
}

/// <summary>
/// Result of an error boundary execution.
/// </summary>
public class ErrorBoundaryResult
{
    public bool Success { get; set; }
    public string OperationName { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public Exception? Exception { get; set; }
    public bool Cancelled { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
    public TimeSpan Duration => CompletedAt - StartedAt;
}

/// <summary>
/// Result of an error boundary execution with a return value.
/// </summary>
public class ErrorBoundaryResult<T> : ErrorBoundaryResult
{
    public T? Value { get; set; }
    public bool UsedFallback { get; set; }
}

/// <summary>
/// Exception thrown by error boundaries for critical errors.
/// </summary>
public class ErrorBoundaryException : Exception
{
    public ErrorBoundaryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Classification of error severity.
/// </summary>
public enum ErrorSeverity
{
    Warning,
    Error,
    Critical
}
