using System.Text.Json;
using Modular.FluentHttp.Interfaces;

namespace Modular.FluentHttp.Implementation;

/// <summary>
/// Implementation of IResponse wrapping HttpResponseMessage.
/// </summary>
public class FluentResponse : IResponse
{
    private readonly HttpResponseMessage _response;
    private readonly string _requestUrl;
    private readonly TimeSpan _elapsed;
    private string? _cachedBody;
    private byte[]? _cachedBytes;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public FluentResponse(HttpResponseMessage response, string requestUrl, TimeSpan elapsed)
    {
        _response = response;
        _requestUrl = requestUrl;
        _elapsed = elapsed;
    }

    public bool IsSuccessStatusCode => _response.IsSuccessStatusCode;
    public int StatusCode => (int)_response.StatusCode;
    public string StatusReason => _response.ReasonPhrase ?? string.Empty;

    public IReadOnlyDictionary<string, IEnumerable<string>> Headers =>
        _response.Headers.Concat(_response.Content.Headers)
            .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.SelectMany(h => h.Value), StringComparer.OrdinalIgnoreCase);

    public string? GetHeader(string name) =>
        Headers.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;

    public bool HasHeader(string name) => Headers.ContainsKey(name);

    public string? ContentType => _response.Content.Headers.ContentType?.MediaType;
    public long? ContentLength => _response.Content.Headers.ContentLength;

    public string AsString()
    {
        if (_cachedBody != null) return _cachedBody;
        _cachedBody = _response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return _cachedBody;
    }

    public byte[] AsByteArray()
    {
        if (_cachedBytes != null) return _cachedBytes;
        _cachedBytes = _response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        return _cachedBytes;
    }

    public JsonDocument AsJson() => JsonDocument.Parse(AsString());

    public T As<T>() => JsonSerializer.Deserialize<T>(AsString(), JsonOptions)!;

    public List<T> AsArray<T>() => JsonSerializer.Deserialize<List<T>>(AsString(), JsonOptions) ?? [];

    public async Task<string> AsStringAsync()
    {
        if (_cachedBody != null) return _cachedBody;
        _cachedBody = await _response.Content.ReadAsStringAsync();
        return _cachedBody;
    }

    public async Task<byte[]> AsByteArrayAsync()
    {
        if (_cachedBytes != null) return _cachedBytes;
        _cachedBytes = await _response.Content.ReadAsByteArrayAsync();
        return _cachedBytes;
    }

    public async Task<JsonDocument> AsJsonAsync() => JsonDocument.Parse(await AsStringAsync());

    public void SaveToFile(string path, IProgress<(long downloaded, long total)>? progress = null)
    {
        SaveToFileAsync(path, progress).GetAwaiter().GetResult();
    }

    public async Task SaveToFileAsync(string path, IProgress<(long downloaded, long total)>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var totalBytes = ContentLength ?? -1;
        var downloadedBytes = 0L;

        await using var contentStream = await _response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        int bytesRead;
        var lastUpdate = DateTime.UtcNow;

        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            downloadedBytes += bytesRead;

            if ((DateTime.UtcNow - lastUpdate).TotalMilliseconds >= 100)
            {
                progress?.Report((downloadedBytes, totalBytes));
                lastUpdate = DateTime.UtcNow;
            }
        }
        progress?.Report((downloadedBytes, totalBytes));
    }

    public string EffectiveUrl => _response.RequestMessage?.RequestUri?.ToString() ?? _requestUrl;
    public TimeSpan Elapsed => _elapsed;
    public bool WasRedirected => _response.RequestMessage?.RequestUri?.ToString() != _requestUrl;
}
