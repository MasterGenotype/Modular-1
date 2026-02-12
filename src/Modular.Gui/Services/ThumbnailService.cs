using System.Collections.Concurrent;
using Avalonia.Media.Imaging;

namespace Modular.Gui.Services;

/// <summary>
/// Service for downloading and caching mod thumbnails.
/// </summary>
public class ThumbnailService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _cacheDirectory;
    private readonly ConcurrentDictionary<string, Bitmap?> _memoryCache = new();
    private readonly SemaphoreSlim _downloadSemaphore = new(3, 3); // Max 3 concurrent downloads
    private bool _disposed;

    public ThumbnailService(string cacheDirectory)
    {
        _cacheDirectory = cacheDirectory;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Modular/1.0");
        _httpClient.Timeout = TimeSpan.FromSeconds(10);

        if (!Directory.Exists(_cacheDirectory))
        {
            Directory.CreateDirectory(_cacheDirectory);
        }
    }

    /// <summary>
    /// Gets a thumbnail for the given URL, using cache if available.
    /// </summary>
    /// <param name="url">The thumbnail URL.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A Bitmap if successful, null otherwise.</returns>
    public async Task<Bitmap?> GetThumbnailAsync(string? url, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(url))
            return null;

        // Check memory cache first
        if (_memoryCache.TryGetValue(url, out var cached))
            return cached;

        // Check disk cache
        var cacheKey = GetCacheKey(url);
        var cachePath = Path.Combine(_cacheDirectory, cacheKey);

        if (File.Exists(cachePath))
        {
            try
            {
                var bitmap = new Bitmap(cachePath);
                _memoryCache[url] = bitmap;
                return bitmap;
            }
            catch
            {
                // Cache file corrupted, delete and re-download
                try { File.Delete(cachePath); } catch { }
            }
        }

        // Download the thumbnail
        await _downloadSemaphore.WaitAsync(ct);
        try
        {
            // Double-check cache after acquiring semaphore
            if (_memoryCache.TryGetValue(url, out cached))
                return cached;

            var response = await _httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _memoryCache[url] = null;
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);

            // Save to disk cache
            await File.WriteAllBytesAsync(cachePath, bytes, ct);

            // Load as bitmap
            using var stream = new MemoryStream(bytes);
            var bitmap = new Bitmap(stream);
            _memoryCache[url] = bitmap;
            return bitmap;
        }
        catch (Exception)
        {
            _memoryCache[url] = null;
            return null;
        }
        finally
        {
            _downloadSemaphore.Release();
        }
    }

    /// <summary>
    /// Preloads thumbnails for a collection of URLs.
    /// </summary>
    public async Task PreloadAsync(IEnumerable<string?> urls, CancellationToken ct = default)
    {
        var tasks = urls
            .Where(u => !string.IsNullOrEmpty(u))
            .Select(u => GetThumbnailAsync(u, ct));

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Clears the memory cache.
    /// </summary>
    public void ClearMemoryCache()
    {
        foreach (var bitmap in _memoryCache.Values)
        {
            bitmap?.Dispose();
        }
        _memoryCache.Clear();
    }

    /// <summary>
    /// Clears the disk cache.
    /// </summary>
    public void ClearDiskCache()
    {
        try
        {
            if (Directory.Exists(_cacheDirectory))
            {
                foreach (var file in Directory.GetFiles(_cacheDirectory))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Gets the cache size in bytes.
    /// </summary>
    public long GetCacheSizeBytes()
    {
        try
        {
            if (!Directory.Exists(_cacheDirectory))
                return 0;

            return Directory.GetFiles(_cacheDirectory)
                .Select(f => new FileInfo(f).Length)
                .Sum();
        }
        catch
        {
            return 0;
        }
    }

    private static string GetCacheKey(string url)
    {
        // Create a safe filename from the URL hash
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(hash)[..32] + ".jpg";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ClearMemoryCache();
        _httpClient.Dispose();
        _downloadSemaphore.Dispose();
    }
}
