using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Modular.FluentHttp.Interfaces;

namespace Modular.FluentHttp.Implementation;

/// <summary>
/// Fluent HTTP client implementation.
/// </summary>
public class FluentClient : IFluentClient
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private ILogger? _logger;
    private string _baseUrl = string.Empty;
    private string? _authScheme;
    private string? _authParameter;
    private IRetryConfig _retryConfig = new DefaultRetryConfig();
    private bool _disposed;

    public FluentClient(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient == null;
        _httpClient = httpClient ?? new HttpClient();
        // Set a default timeout of 30 seconds to prevent indefinite hangs
        // Only override if using a new HttpClient (default is 100 seconds which is too long)
        if (_ownsHttpClient)
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public string BaseUrl => _baseUrl;
    public RequestOptions Options { get; private set; } = new();
    public FilterCollection Filters { get; } = new();
    public IRateLimiter? RateLimiter { get; private set; }

    public IRequest GetAsync(string resource) => new FluentRequest(this, HttpMethod.Get, resource);
    public IRequest PostAsync(string resource) => new FluentRequest(this, HttpMethod.Post, resource);
    public IRequest PutAsync(string resource) => new FluentRequest(this, HttpMethod.Put, resource);
    public IRequest PatchAsync(string resource) => new FluentRequest(this, HttpMethod.Patch, resource);
    public IRequest DeleteAsync(string resource) => new FluentRequest(this, HttpMethod.Delete, resource);
    public IRequest HeadAsync(string resource) => new FluentRequest(this, HttpMethod.Head, resource);
    public IRequest SendAsync(HttpMethod method, string resource) => new FluentRequest(this, method, resource);

    public IFluentClient SetBaseUrl(string baseUrl) { _baseUrl = baseUrl; return this; }
    public IFluentClient SetOptions(RequestOptions options) { Options = options; return this; }
    public IFluentClient SetUserAgent(string userAgent)
    {
        _httpClient.DefaultRequestHeaders.UserAgent.Clear();
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        return this;
    }

    public IFluentClient SetAuthentication(string scheme, string parameter)
    {
        _authScheme = scheme;
        _authParameter = parameter;
        return this;
    }
    public IFluentClient SetBearerAuth(string token) => SetAuthentication("Bearer", token);
    public IFluentClient SetBasicAuth(string username, string password)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        return SetAuthentication("Basic", encoded);
    }
    public IFluentClient ClearAuthentication() { _authScheme = null; _authParameter = null; return this; }

    public IFluentClient AddFilter(IHttpFilter filter) { Filters.Add(filter); return this; }

    public IFluentClient SetRetryPolicy(int maxRetries, int initialDelayMs = 1000, int maxDelayMs = 16000, bool exponentialBackoff = true)
    {
        _retryConfig = new DefaultRetryConfig
        {
            MaxRetries = maxRetries,
            InitialDelayMs = initialDelayMs,
            MaxDelayMs = maxDelayMs,
            ExponentialBackoff = exponentialBackoff
        };
        return this;
    }
    public IFluentClient DisableRetries() { _retryConfig = new DefaultRetryConfig { MaxRetries = 0 }; return this; }

    public IFluentClient SetRateLimiter(IRateLimiter? rateLimiter) { RateLimiter = rateLimiter; return this; }
    public IFluentClient SetConnectionTimeout(TimeSpan timeout) { _httpClient.Timeout = timeout; return this; }
    public IFluentClient SetRequestTimeout(TimeSpan timeout) { _httpClient.Timeout = timeout; return this; }
    public IFluentClient SetLogger(ILogger? logger) { _logger = logger; return this; }

    internal async Task<IResponse> ExecuteAsync(FluentRequest request, string url, HttpMethod method,
        Dictionary<string, string> headers, RequestBody? body, RequestOptions options, IRetryConfig? retryConfig,
        string? authScheme, string? authParameter, List<IHttpFilter> requestFilters, CancellationToken ct)
    {
        var config = retryConfig ?? _retryConfig;
        var maxRetries = options.NoRetry ? 0 : config.MaxRetries;
        Exception? lastException = null;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                // Wait for rate limiter and reserve a slot
                if (RateLimiter != null)
                {
                    await RateLimiter.WaitIfNeededAsync(ct);
                    RateLimiter.ReserveRequest();
                }

                using var httpRequest = new HttpRequestMessage(method, url);

                // Apply body
                if (body?.Content != null)
                    httpRequest.Content = body.Content;

                // Apply headers
                foreach (var (key, value) in headers)
                    httpRequest.Headers.TryAddWithoutValidation(key, value);

                // Apply authentication
                var scheme = authScheme ?? _authScheme;
                var param = authParameter ?? _authParameter;
                if (!string.IsNullOrEmpty(scheme) && !string.IsNullOrEmpty(param))
                    httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(scheme, param);

                // Apply filters
                var allFilters = Filters.GetOrdered().Concat(requestFilters).ToList();
                foreach (var filter in allFilters)
                    filter.OnRequest(request);

                var stopwatch = Stopwatch.StartNew();
                var response = await _httpClient.SendAsync(httpRequest, ct);
                stopwatch.Stop();

                // Update rate limiter
                RateLimiter?.UpdateFromHeaders(response.Headers);

                var fluentResponse = new FluentResponse(response, url, stopwatch.Elapsed);

                // Apply response filters
                foreach (var filter in allFilters)
                    filter.OnResponse(fluentResponse, !options.IgnoreHttpErrors);

                // Check for retry
                if (!response.IsSuccessStatusCode && config.ShouldRetry((int)response.StatusCode, false) && attempt < maxRetries)
                {
                    var delay = config.GetDelay(attempt);
                    _logger?.LogWarning("Request to {Url} failed with {StatusCode}, retrying in {Delay}ms",
                        url, (int)response.StatusCode, delay.TotalMilliseconds);
                    await Task.Delay(delay, ct);
                    continue;
                }

                return fluentResponse;
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
                if (attempt < maxRetries)
                {
                    var delay = config.GetDelay(attempt);
                    _logger?.LogWarning(ex, "Request to {Url} failed, retrying in {Delay}ms", url, delay.TotalMilliseconds);
                    await Task.Delay(delay, ct);
                }
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                if (config.ShouldRetry(0, true) && attempt < maxRetries)
                {
                    var delay = config.GetDelay(attempt);
                    _logger?.LogWarning("Request to {Url} timed out, retrying in {Delay}ms", url, delay.TotalMilliseconds);
                    await Task.Delay(delay, ct);
                }
                else throw;
            }
        }

        throw new HttpRequestException($"Request to {url} failed after {maxRetries} retries", lastException);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
            _disposed = true;
        }
    }
}

/// <summary>
/// Factory for creating FluentClient instances.
/// </summary>
public static class FluentClientFactory
{
    public static IFluentClient Create(string? baseUrl = null)
    {
        var client = new FluentClient();
        if (!string.IsNullOrEmpty(baseUrl))
            client.SetBaseUrl(baseUrl);
        return client;
    }

    public static IFluentClient Create(string baseUrl, IRateLimiter? rateLimiter, ILogger? logger = null)
    {
        var client = new FluentClient();
        client.SetBaseUrl(baseUrl);
        client.SetRateLimiter(rateLimiter);
        client.SetLogger(logger);
        return client;
    }
}
