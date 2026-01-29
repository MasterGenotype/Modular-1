using System.Text.Json;

namespace Modular.FluentHttp.Interfaces;

/// <summary>
/// Represents an HTTP response with fluent access to data.
/// </summary>
public interface IResponse
{
    /// <summary>
    /// Whether the response indicates success (2xx status code).
    /// </summary>
    bool IsSuccessStatusCode { get; }

    /// <summary>
    /// HTTP status code.
    /// </summary>
    int StatusCode { get; }

    /// <summary>
    /// HTTP status reason phrase.
    /// </summary>
    string StatusReason { get; }

    /// <summary>
    /// Response headers.
    /// </summary>
    IReadOnlyDictionary<string, IEnumerable<string>> Headers { get; }

    /// <summary>
    /// Gets a header value by name.
    /// </summary>
    string? GetHeader(string name);

    /// <summary>
    /// Checks if a header exists.
    /// </summary>
    bool HasHeader(string name);

    /// <summary>
    /// Content-Type header value.
    /// </summary>
    string? ContentType { get; }

    /// <summary>
    /// Content-Length header value.
    /// </summary>
    long? ContentLength { get; }

    /// <summary>
    /// Gets the response body as a string.
    /// </summary>
    string AsString();

    /// <summary>
    /// Gets the response body as a byte array.
    /// </summary>
    byte[] AsByteArray();

    /// <summary>
    /// Gets the response body as a JSON document.
    /// </summary>
    JsonDocument AsJson();

    /// <summary>
    /// Deserializes the response body to a typed object.
    /// </summary>
    T As<T>();

    /// <summary>
    /// Deserializes the response body to a list.
    /// </summary>
    List<T> AsArray<T>();

    /// <summary>
    /// Gets the response body as a string asynchronously.
    /// </summary>
    Task<string> AsStringAsync();

    /// <summary>
    /// Gets the response body as a byte array asynchronously.
    /// </summary>
    Task<byte[]> AsByteArrayAsync();

    /// <summary>
    /// Gets the response body as a JSON document asynchronously.
    /// </summary>
    Task<JsonDocument> AsJsonAsync();

    /// <summary>
    /// Saves the response body to a file.
    /// </summary>
    void SaveToFile(string path, IProgress<(long downloaded, long total)>? progress = null);

    /// <summary>
    /// Saves the response body to a file asynchronously.
    /// </summary>
    Task SaveToFileAsync(string path, IProgress<(long downloaded, long total)>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// The final URL after any redirects.
    /// </summary>
    string EffectiveUrl { get; }

    /// <summary>
    /// Time elapsed for the request.
    /// </summary>
    TimeSpan Elapsed { get; }

    /// <summary>
    /// Whether the request was redirected.
    /// </summary>
    bool WasRedirected { get; }
}
