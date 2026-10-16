using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Modular.Core.Backends.ModIo;

/// <summary>
/// The mod.io REST API, version 1.
/// </summary>
/// <remarks>
/// Every request goes to the game's own host, <c>g-{gameId}.modapi.io</c>: the shared
/// <c>api.mod.io</c> host is deprecated and answers 404 to everything. An OAuth access
/// token, when there is one, is sent as a bearer token and covers every endpoint; the
/// API key only reads public data and goes in the query string.
/// </remarks>
public sealed class ModIoClient : IDisposable
{
    /// <summary>Largest page mod.io serves.</summary>
    public const int MaxPageSize = 100;

    // mod.io allows 60 requests a minute for an API key; one a second stays under it
    // whichever credential is in use.
    private static readonly TimeSpan DefaultThrottle = TimeSpan.FromSeconds(1);
    private const int MaxRateLimitRetries = 2;

    private readonly HttpClient _http;
    private readonly Func<string> _apiKey;
    private readonly Func<string> _accessToken;
    private readonly Func<long, string> _baseUrl;
    private readonly TimeSpan _throttleDelay;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _throttle = new(1, 1);
    private DateTime _lastRequest = DateTime.MinValue;

    /// <summary>
    /// Creates a client.
    /// </summary>
    /// <param name="apiKey">Reads the current API key, so a settings change applies at once.</param>
    /// <param name="accessToken">Reads the current OAuth access token.</param>
    /// <param name="handler">HTTP handler, for tests.</param>
    /// <param name="baseUrl">The API root for a game id; defaults to the game's modapi.io host.</param>
    /// <param name="throttleDelay">Minimum gap between requests.</param>
    /// <param name="logger">Optional logger.</param>
    public ModIoClient(
        Func<string> apiKey,
        Func<string> accessToken,
        HttpMessageHandler? handler = null,
        Func<long, string>? baseUrl = null,
        TimeSpan? throttleDelay = null,
        ILogger? logger = null)
    {
        _apiKey = apiKey;
        _accessToken = accessToken;
        _baseUrl = baseUrl ?? (gameId => $"https://g-{gameId}.modapi.io/v1");
        _throttleDelay = throttleDelay ?? DefaultThrottle;
        _logger = logger;

        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Modular/1.0");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>Whether an OAuth access token is configured.</summary>
    public bool HasAccessToken => !string.IsNullOrWhiteSpace(_accessToken());

    /// <summary>Whether either credential is configured.</summary>
    public bool HasCredentials => HasAccessToken || !string.IsNullOrWhiteSpace(_apiKey());

    /// <summary>
    /// Gets one object.
    /// </summary>
    /// <typeparam name="T">Response type.</typeparam>
    /// <param name="gameId">Game whose host serves the request.</param>
    /// <param name="path">Path below the API root, without a leading slash.</param>
    /// <param name="query">Query parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The response object.</returns>
    public async Task<T> GetAsync<T>(
        long gameId,
        string path,
        IReadOnlyDictionary<string, string>? query = null,
        CancellationToken ct = default)
    {
        using var document = await SendAsync(gameId, path, query, ct);
        return document.Deserialize<T>()
            ?? throw new ModIoException($"mod.io returned an empty response for {path}.", HttpStatusCode.OK);
    }

    /// <summary>
    /// Gets every page of a list endpoint.
    /// </summary>
    /// <typeparam name="T">Item type.</typeparam>
    /// <param name="gameId">Game whose host serves the request.</param>
    /// <param name="path">Path below the API root.</param>
    /// <param name="query">Query parameters; paging is added here.</param>
    /// <param name="maxItems">Stop after this many items.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The items, in the order served.</returns>
    public async Task<List<T>> GetAllAsync<T>(
        long gameId,
        string path,
        IReadOnlyDictionary<string, string>? query = null,
        int maxItems = int.MaxValue,
        CancellationToken ct = default)
    {
        var items = new List<T>();
        var offset = 0;

        while (items.Count < maxItems)
        {
            var paged = new Dictionary<string, string>(query ?? new Dictionary<string, string>())
            {
                ["_limit"] = Math.Min(MaxPageSize, maxItems - items.Count).ToString(CultureInfo.InvariantCulture),
                ["_offset"] = offset.ToString(CultureInfo.InvariantCulture)
            };

            var page = await GetAsync<ModIoPage<T>>(gameId, path, paged, ct);
            items.AddRange(page.Data);

            offset += page.Data.Count;
            if (page.Data.Count == 0 || offset >= page.ResultTotal)
                break;
        }

        return items;
    }

    private async Task<JsonDocument> SendAsync(
        long gameId,
        string path,
        IReadOnlyDictionary<string, string>? query,
        CancellationToken ct)
    {
        var token = _accessToken();
        var key = _apiKey();

        if (string.IsNullOrWhiteSpace(token) && string.IsNullOrWhiteSpace(key))
        {
            throw new ModIoException(
                "mod.io needs an API key or an access token. Create one at https://mod.io/me/access " +
                "and set 'modio_api_key' or 'modio_access_token' (or MODIO_API_KEY / MODIO_ACCESS_TOKEN).",
                HttpStatusCode.Unauthorized);
        }

        var parameters = new Dictionary<string, string>(query ?? new Dictionary<string, string>());
        if (string.IsNullOrWhiteSpace(token))
            parameters["api_key"] = key;

        var url = $"{_baseUrl(gameId).TrimEnd('/')}/{path}";
        if (parameters.Count > 0)
        {
            url += "?" + string.Join("&", parameters.Select(p =>
                $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        }

        for (var attempt = 0; ; attempt++)
        {
            await ThrottleAsync(ct);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await _http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxRateLimitRetries)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60);
                _logger?.LogWarning("mod.io rate limit reached; waiting {Seconds}s", wait.TotalSeconds);
                await Task.Delay(wait, ct);
                continue;
            }

            if (!response.IsSuccessStatusCode)
                throw ModIoException.FromResponse(response.StatusCode, body, path);

            return JsonDocument.Parse(body);
        }
    }

    private async Task ThrottleAsync(CancellationToken ct)
    {
        await _throttle.WaitAsync(ct);
        try
        {
            var elapsed = DateTime.UtcNow - _lastRequest;
            if (elapsed < _throttleDelay)
                await Task.Delay(_throttleDelay - elapsed, ct);
            _lastRequest = DateTime.UtcNow;
        }
        finally
        {
            _throttle.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _http.Dispose();
        _throttle.Dispose();
    }
}

/// <summary>
/// A failed mod.io request, carrying the message mod.io gave.
/// </summary>
public sealed class ModIoException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ModIoException(string message, HttpStatusCode statusCode, int? errorRef = null)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorRef = errorRef;
    }

    /// <summary>HTTP status of the response.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>mod.io's own error reference, such as 11005 for a bad access token.</summary>
    public int? ErrorRef { get; }

    internal static ModIoException FromResponse(HttpStatusCode status, string body, string path)
    {
        ModIoError? error = null;
        try
        {
            error = JsonSerializer.Deserialize<ModIoErrorEnvelope>(body)?.Error;
        }
        catch (JsonException)
        {
            // Not mod.io's envelope; the status alone has to do.
        }

        var message = error?.Message ?? $"HTTP {(int)status}";
        return new ModIoException($"mod.io request '{path}' failed: {message}", status, error?.ErrorRef);
    }
}
