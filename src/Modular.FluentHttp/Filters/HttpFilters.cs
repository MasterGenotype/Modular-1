using Microsoft.Extensions.Logging;
using Modular.FluentHttp.Interfaces;

namespace Modular.FluentHttp.Filters;

/// <summary>
/// Filter that adds authentication headers to requests.
/// </summary>
public class AuthenticationFilter : IHttpFilter
{
    private readonly string _headerName;
    private readonly string _headerValue;

    public AuthenticationFilter(string apiKey, string headerName = "apikey")
    {
        _headerName = headerName;
        _headerValue = apiKey;
    }

    public string Name => "Authentication";
    public int Priority => 10;

    public void OnRequest(IRequest request)
    {
        request.WithHeader(_headerName, _headerValue);
    }

    public void OnResponse(IResponse response, bool httpErrorAsException) { }
}

/// <summary>
/// Filter that enforces rate limiting.
/// </summary>
public class RateLimitFilter : IHttpFilter
{
    private readonly IRateLimiter _rateLimiter;

    public RateLimitFilter(IRateLimiter rateLimiter)
    {
        _rateLimiter = rateLimiter;
    }

    public string Name => "RateLimit";
    public int Priority => 5;

    public void OnRequest(IRequest request)
    {
        // Rate limiting is handled in the client execution
    }

    public void OnResponse(IResponse response, bool httpErrorAsException)
    {
        _rateLimiter.UpdateFromHeaders(response.Headers.Select(h =>
            new KeyValuePair<string, IEnumerable<string>>(h.Key, h.Value)));
    }
}

/// <summary>
/// Filter that logs request and response details.
/// </summary>
public class LoggingFilter : IHttpFilter
{
    private readonly ILogger _logger;

    public LoggingFilter(ILogger logger)
    {
        _logger = logger;
    }

    public string Name => "Logging";
    public int Priority => 100;

    public void OnRequest(IRequest request)
    {
        _logger.LogDebug("Sending request");
    }

    public void OnResponse(IResponse response, bool httpErrorAsException)
    {
        if (response.IsSuccessStatusCode)
        {
            _logger.LogDebug("Response: {StatusCode} in {Elapsed}ms",
                response.StatusCode, response.Elapsed.TotalMilliseconds);
        }
        else
        {
            _logger.LogWarning("Response: {StatusCode} {Reason}",
                response.StatusCode, response.StatusReason);
        }
    }
}

/// <summary>
/// Filter that throws exceptions for HTTP errors.
/// </summary>
public class ErrorFilter : IHttpFilter
{
    public string Name => "Error";
    public int Priority => 200;

    public void OnRequest(IRequest request) { }

    public void OnResponse(IResponse response, bool httpErrorAsException)
    {
        if (httpErrorAsException && !response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {response.StatusCode}: {response.StatusReason}");
        }
    }
}
