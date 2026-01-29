using System.Text.Json;
using Modular.FluentHttp.Implementation;

namespace Modular.FluentHttp.Interfaces;

/// <summary>
/// Fluent interface for building and executing HTTP requests.
/// </summary>
public interface IRequest
{
    // URL parameters
    IRequest WithArgument(string key, string value);
    IRequest WithArguments(IEnumerable<KeyValuePair<string, string>> args);

    // Headers
    IRequest WithHeader(string key, string value);
    IRequest WithHeaders(IDictionary<string, string> headers);
    IRequest WithoutHeader(string key);

    // Authentication
    IRequest WithAuthentication(string scheme, string parameter);
    IRequest WithBearerAuth(string token);
    IRequest WithBasicAuth(string username, string password);

    // Body
    IRequest WithBody(Func<IBodyBuilder, RequestBody> builder);
    IRequest WithBody(RequestBody body);
    IRequest WithJsonBody<T>(T value);
    IRequest WithFormBody(IEnumerable<KeyValuePair<string, string>> fields);

    // Options
    IRequest WithOptions(RequestOptions options);
    IRequest WithIgnoreHttpErrors(bool ignore = true);
    IRequest WithTimeout(TimeSpan timeout);
    IRequest WithCancellation(CancellationToken token);

    // Filters
    IRequest WithFilter(IHttpFilter filter);
    IRequest WithoutFilter(IHttpFilter filter);
    IRequest WithRetryConfig(IRetryConfig config);
    IRequest WithNoRetry();

    // Synchronous execution
    IResponse AsResponse();
    string AsString();
    JsonDocument AsJson();
    T As<T>();
    List<T> AsArray<T>();
    void DownloadTo(string path, IProgress<(long downloaded, long total)>? progress = null);

    // Async execution
    Task<IResponse> AsResponseAsync();
    Task<string> AsStringAsync();
    Task<JsonDocument> AsJsonAsync();
    Task<T> AsAsync<T>();
    Task<List<T>> AsArrayAsync<T>();
    Task DownloadToAsync(string path, IProgress<(long downloaded, long total)>? progress = null, CancellationToken ct = default);
}
