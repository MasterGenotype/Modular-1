using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modular.Core.Http;

/// <summary>
/// HTTP cache supporting ETags and conditional requests.
/// Reduces bandwidth usage and improves performance.
/// </summary>
public class HttpCache
{
    private readonly string _cachePath;
    private readonly Dictionary<string, CacheEntry> _entries = new();
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public HttpCache(string cachePath)
    {
        _cachePath = cachePath;
    }

    /// <summary>
    /// Gets cached entry for a URL.
    /// </summary>
    public CacheEntry? Get(string url)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(url, out var entry))
            {
                // Check if entry is still valid
                if (entry.ExpiresAt.HasValue && entry.ExpiresAt.Value < DateTime.UtcNow)
                {
                    _entries.Remove(url);
                    return null;
                }
                return entry;
            }
            return null;
        }
    }

    /// <summary>
    /// Sets cache entry for a URL.
    /// </summary>
    public void Set(string url, CacheEntry entry)
    {
        lock (_lock)
        {
            _entries[url] = entry;
        }
    }

    /// <summary>
    /// Removes cache entry for a URL.
    /// </summary>
    public void Remove(string url)
    {
        lock (_lock)
        {
            _entries.Remove(url);
        }
    }

    /// <summary>
    /// Clears all cache entries.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }

    /// <summary>
    /// Gets conditional request headers for a URL.
    /// </summary>
    public Dictionary<string, string>? GetConditionalHeaders(string url)
    {
        var entry = Get(url);
        if (entry == null)
            return null;

        var headers = new Dictionary<string, string>();

        if (!string.IsNullOrEmpty(entry.ETag))
            headers["If-None-Match"] = entry.ETag;

        if (entry.LastModified.HasValue)
            headers["If-Modified-Since"] = entry.LastModified.Value.ToString("R");

        return headers.Count > 0 ? headers : null;
    }

    /// <summary>
    /// Saves cache to disk.
    /// </summary>
    public async Task SaveAsync()
    {
        Dictionary<string, CacheEntry> snapshot;
        lock (_lock)
        {
            snapshot = new Dictionary<string, CacheEntry>(_entries);
        }

        var directory = Path.GetDirectoryName(_cachePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        await File.WriteAllTextAsync(_cachePath, json);
    }

    /// <summary>
    /// Loads cache from disk.
    /// </summary>
    public async Task LoadAsync()
    {
        if (!File.Exists(_cachePath))
            return;

        try
        {
            var json = await File.ReadAllTextAsync(_cachePath);
            var entries = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json, JsonOptions);

            if (entries != null)
            {
                lock (_lock)
                {
                    _entries.Clear();
                    foreach (var (key, value) in entries)
                    {
                        // Skip expired entries
                        if (value.ExpiresAt.HasValue && value.ExpiresAt.Value < DateTime.UtcNow)
                            continue;

                        _entries[key] = value;
                    }
                }
            }
        }
        catch
        {
            // Ignore load errors
        }
    }

    /// <summary>
    /// Removes expired entries.
    /// </summary>
    public int RemoveExpired()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var expired = _entries
                .Where(kvp => kvp.Value.ExpiresAt.HasValue && kvp.Value.ExpiresAt.Value < now)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expired)
                _entries.Remove(key);

            return expired.Count;
        }
    }
}

/// <summary>
/// HTTP cache entry with ETag and expiration.
/// </summary>
public class CacheEntry
{
    /// <summary>
    /// ETag from response.
    /// </summary>
    [JsonPropertyName("etag")]
    public string? ETag { get; set; }

    /// <summary>
    /// Last-Modified timestamp.
    /// </summary>
    [JsonPropertyName("last_modified")]
    public DateTime? LastModified { get; set; }

    /// <summary>
    /// When this entry expires.
    /// </summary>
    [JsonPropertyName("expires_at")]
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// When this entry was cached.
    /// </summary>
    [JsonPropertyName("cached_at")]
    public DateTime CachedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Response status code.
    /// </summary>
    [JsonPropertyName("status_code")]
    public int StatusCode { get; set; }

    /// <summary>
    /// Cached content length.
    /// </summary>
    [JsonPropertyName("content_length")]
    public long? ContentLength { get; set; }

    /// <summary>
    /// Content type.
    /// </summary>
    [JsonPropertyName("content_type")]
    public string? ContentType { get; set; }

    /// <summary>
    /// Creates cache entry from HTTP response headers.
    /// </summary>
    public static CacheEntry FromHeaders(IDictionary<string, IEnumerable<string>> headers, int statusCode)
    {
        var entry = new CacheEntry
        {
            StatusCode = statusCode
        };

        // Get ETag
        if (headers.TryGetValue("ETag", out var etagValues))
            entry.ETag = etagValues.FirstOrDefault();

        // Get Last-Modified
        if (headers.TryGetValue("Last-Modified", out var lastModValues))
        {
            var lastModStr = lastModValues.FirstOrDefault();
            if (!string.IsNullOrEmpty(lastModStr) && 
                DateTime.TryParse(lastModStr, out var lastMod))
            {
                entry.LastModified = lastMod.ToUniversalTime();
            }
        }

        // Calculate expiration from Cache-Control or Expires
        if (headers.TryGetValue("Cache-Control", out var cacheControlValues))
        {
            var cacheControl = cacheControlValues.FirstOrDefault();
            if (!string.IsNullOrEmpty(cacheControl))
            {
                // Parse max-age directive
                var maxAgeMatch = System.Text.RegularExpressions.Regex.Match(cacheControl, @"max-age=(\d+)");
                if (maxAgeMatch.Success && int.TryParse(maxAgeMatch.Groups[1].Value, out var maxAge))
                {
                    entry.ExpiresAt = DateTime.UtcNow.AddSeconds(maxAge);
                }
            }
        }
        else if (headers.TryGetValue("Expires", out var expiresValues))
        {
            var expiresStr = expiresValues.FirstOrDefault();
            if (!string.IsNullOrEmpty(expiresStr) && 
                DateTime.TryParse(expiresStr, out var expires))
            {
                entry.ExpiresAt = expires.ToUniversalTime();
            }
        }

        // Get Content-Length
        if (headers.TryGetValue("Content-Length", out var lengthValues))
        {
            var lengthStr = lengthValues.FirstOrDefault();
            if (!string.IsNullOrEmpty(lengthStr) && long.TryParse(lengthStr, out var length))
                entry.ContentLength = length;
        }

        // Get Content-Type
        if (headers.TryGetValue("Content-Type", out var typeValues))
            entry.ContentType = typeValues.FirstOrDefault();

        return entry;
    }
}

/// <summary>
/// Extension methods for HttpClient with caching support.
/// </summary>
public static class HttpClientCacheExtensions
{
    /// <summary>
    /// Sends a GET request with conditional headers from cache.
    /// </summary>
    public static async Task<CachedHttpResponse> GetWithCacheAsync(
        this HttpClient client,
        string url,
        HttpCache cache,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        // Add conditional headers from cache
        var conditionalHeaders = cache.GetConditionalHeaders(url);
        if (conditionalHeaders != null)
        {
            foreach (var (key, value) in conditionalHeaders)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        // 304 Not Modified - content hasn't changed
        if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
        {
            var cachedEntry = cache.Get(url);
            return new CachedHttpResponse
            {
                StatusCode = (int)response.StatusCode,
                IsFromCache = true,
                CacheEntry = cachedEntry
            };
        }

        // Update cache with new response
        var headers = response.Headers
            .Concat(response.Content.Headers)
            .ToDictionary(h => h.Key, h => h.Value);

        var entry = CacheEntry.FromHeaders(headers, (int)response.StatusCode);
        cache.Set(url, entry);

        return new CachedHttpResponse
        {
            StatusCode = (int)response.StatusCode,
            Response = response,
            IsFromCache = false,
            CacheEntry = entry
        };
    }
}

/// <summary>
/// Response from cached HTTP request.
/// </summary>
public class CachedHttpResponse : IDisposable
{
    public int StatusCode { get; set; }
    public bool IsFromCache { get; set; }
    public HttpResponseMessage? Response { get; set; }
    public CacheEntry? CacheEntry { get; set; }

    public void Dispose()
    {
        Response?.Dispose();
    }
}
