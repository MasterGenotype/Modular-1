using Microsoft.Extensions.Logging;
using Modular.FluentHttp.Implementation;

namespace Modular.FluentHttp.Interfaces;

/// <summary>
/// Fluent HTTP client interface for making API requests.
/// </summary>
public interface IFluentClient : IDisposable
{
    // HTTP Methods
    IRequest GetAsync(string resource);
    IRequest PostAsync(string resource);
    IRequest PutAsync(string resource);
    IRequest PatchAsync(string resource);
    IRequest DeleteAsync(string resource);
    IRequest HeadAsync(string resource);
    IRequest SendAsync(HttpMethod method, string resource);

    // Configuration
    IFluentClient SetBaseUrl(string baseUrl);
    string BaseUrl { get; }
    IFluentClient SetOptions(RequestOptions options);
    RequestOptions Options { get; }
    IFluentClient SetUserAgent(string userAgent);

    // Authentication
    IFluentClient SetAuthentication(string scheme, string parameter);
    IFluentClient SetBearerAuth(string token);
    IFluentClient SetBasicAuth(string username, string password);
    IFluentClient ClearAuthentication();

    // Filters
    FilterCollection Filters { get; }
    IFluentClient AddFilter(IHttpFilter filter);

    // Retry
    IFluentClient SetRetryPolicy(int maxRetries, int initialDelayMs = 1000, int maxDelayMs = 16000, bool exponentialBackoff = true);
    IFluentClient DisableRetries();

    // Rate limiting
    IFluentClient SetRateLimiter(IRateLimiter? rateLimiter);
    IRateLimiter? RateLimiter { get; }

    // Timeouts
    IFluentClient SetConnectionTimeout(TimeSpan timeout);
    IFluentClient SetRequestTimeout(TimeSpan timeout);

    // Logging
    IFluentClient SetLogger(ILogger? logger);
}

/// <summary>
/// Interface for rate limiting HTTP requests.
/// </summary>
public interface IRateLimiter
{
    void UpdateFromHeaders(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers);
    bool CanMakeRequest();
    Task WaitIfNeededAsync(CancellationToken ct = default);
}
