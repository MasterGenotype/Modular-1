namespace Modular.Core.Exceptions;

/// <summary>
/// Base exception for all Modular-specific errors.
/// </summary>
public class ModularException : Exception
{
    /// <summary>
    /// The URL associated with this error, if any.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Additional context about where/when the error occurred.
    /// </summary>
    public string? Context { get; set; }

    /// <summary>
    /// A snippet of the response body, if applicable.
    /// </summary>
    public string? ResponseSnippet { get; set; }

    public ModularException() { }
    public ModularException(string message) : base(message) { }
    public ModularException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Exception for network-level errors (connection failures, timeouts, etc.).
/// </summary>
public class NetworkException : ModularException
{
    /// <summary>
    /// HTTP status code if available, or custom error code.
    /// </summary>
    public int? ErrorCode { get; set; }

    public NetworkException() { }
    public NetworkException(string message) : base(message) { }
    public NetworkException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Exception for API-level errors (non-2xx responses).
/// </summary>
public class ApiException : ModularException
{
    /// <summary>
    /// HTTP status code.
    /// </summary>
    public int StatusCode { get; set; }

    /// <summary>
    /// Request ID from the server, if provided.
    /// </summary>
    public string? RequestId { get; set; }

    public ApiException() { }
    public ApiException(string message) : base(message) { }
    public ApiException(string message, int statusCode) : base(message) { StatusCode = statusCode; }
    public ApiException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Exception when rate limits are exceeded.
/// </summary>
public class RateLimitException : ApiException
{
    /// <summary>
    /// Seconds to wait before retrying, if provided by the server.
    /// </summary>
    public int? RetryAfterSeconds { get; set; }

    public RateLimitException() { StatusCode = 429; }
    public RateLimitException(string message) : base(message) { StatusCode = 429; }
    public RateLimitException(string message, int retryAfterSeconds) : base(message)
    {
        StatusCode = 429;
        RetryAfterSeconds = retryAfterSeconds;
    }
}

/// <summary>
/// Exception for authentication/authorization errors.
/// </summary>
public class AuthException : ApiException
{
    public AuthException() { StatusCode = 401; }
    public AuthException(string message) : base(message) { StatusCode = 401; }
    public AuthException(string message, int statusCode) : base(message, statusCode) { }
}

/// <summary>
/// Exception for JSON parsing errors.
/// </summary>
public class ParseException : ModularException
{
    /// <summary>
    /// A snippet of the JSON that failed to parse.
    /// </summary>
    public string? JsonSnippet { get; set; }

    public ParseException() { }
    public ParseException(string message) : base(message) { }
    public ParseException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Exception for file system errors.
/// </summary>
public class FileSystemException : ModularException
{
    /// <summary>
    /// The file path involved in the error.
    /// </summary>
    public string? FilePath { get; set; }

    public FileSystemException() { }
    public FileSystemException(string message) : base(message) { }
    public FileSystemException(string message, string filePath) : base(message) { FilePath = filePath; }
    public FileSystemException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Exception for configuration errors.
/// </summary>
public class ConfigException : ModularException
{
    /// <summary>
    /// The configuration key that caused the error.
    /// </summary>
    public string? ConfigKey { get; set; }

    public ConfigException() { }
    public ConfigException(string message) : base(message) { }
    public ConfigException(string message, string configKey) : base(message) { ConfigKey = configKey; }
    public ConfigException(string message, Exception innerException) : base(message, innerException) { }
}
