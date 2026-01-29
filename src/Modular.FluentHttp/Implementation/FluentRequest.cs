using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Web;
using Modular.FluentHttp.Interfaces;

namespace Modular.FluentHttp.Implementation;

/// <summary>
/// Implementation of IRequest for building and executing HTTP requests.
/// </summary>
public class FluentRequest : IRequest
{
    private readonly FluentClient _client;
    private readonly HttpMethod _method;
    private readonly string _resource;
    private readonly Dictionary<string, string> _queryParams = [];
    private readonly Dictionary<string, string> _headers = [];
    private readonly List<IHttpFilter> _filters = [];
    private RequestBody? _body;
    private RequestOptions _options = new();
    private IRetryConfig? _retryConfig;
    private CancellationToken _cancellationToken;
    private string? _authScheme;
    private string? _authParameter;

    internal FluentRequest(FluentClient client, HttpMethod method, string resource)
    {
        _client = client;
        _method = method;
        _resource = resource;
    }

    public IRequest WithArgument(string key, string value) { _queryParams[key] = value; return this; }
    public IRequest WithArguments(IEnumerable<KeyValuePair<string, string>> args)
    {
        foreach (var (key, value) in args) _queryParams[key] = value;
        return this;
    }

    public IRequest WithHeader(string key, string value) { _headers[key] = value; return this; }
    public IRequest WithHeaders(IDictionary<string, string> headers)
    {
        foreach (var (key, value) in headers) _headers[key] = value;
        return this;
    }
    public IRequest WithoutHeader(string key) { _headers.Remove(key); return this; }

    public IRequest WithAuthentication(string scheme, string parameter)
    {
        _authScheme = scheme;
        _authParameter = parameter;
        return this;
    }
    public IRequest WithBearerAuth(string token) => WithAuthentication("Bearer", token);
    public IRequest WithBasicAuth(string username, string password)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        return WithAuthentication("Basic", encoded);
    }

    public IRequest WithBody(Func<IBodyBuilder, RequestBody> builder)
    {
        _body = builder(new BodyBuilder());
        return this;
    }
    public IRequest WithBody(RequestBody body) { _body = body; return this; }
    public IRequest WithJsonBody<T>(T value) { _body = RequestBody.Json(value); return this; }
    public IRequest WithFormBody(IEnumerable<KeyValuePair<string, string>> fields) { _body = RequestBody.Form(fields); return this; }

    public IRequest WithOptions(RequestOptions options) { _options = options.Clone(); return this; }
    public IRequest WithIgnoreHttpErrors(bool ignore = true) { _options.IgnoreHttpErrors = ignore; return this; }
    public IRequest WithTimeout(TimeSpan timeout) { _options.Timeout = timeout; return this; }
    public IRequest WithCancellation(CancellationToken token) { _cancellationToken = token; return this; }

    public IRequest WithFilter(IHttpFilter filter) { _filters.Add(filter); return this; }
    public IRequest WithoutFilter(IHttpFilter filter) { _filters.Remove(filter); return this; }
    public IRequest WithRetryConfig(IRetryConfig config) { _retryConfig = config; return this; }
    public IRequest WithNoRetry() { _options.NoRetry = true; return this; }

    // Sync wrappers
    public IResponse AsResponse() => AsResponseAsync().GetAwaiter().GetResult();
    public string AsString() => AsStringAsync().GetAwaiter().GetResult();
    public JsonDocument AsJson() => AsJsonAsync().GetAwaiter().GetResult();
    public T As<T>() => AsAsync<T>().GetAwaiter().GetResult();
    public List<T> AsArray<T>() => AsArrayAsync<T>().GetAwaiter().GetResult();
    public void DownloadTo(string path, IProgress<(long downloaded, long total)>? progress = null) =>
        DownloadToAsync(path, progress).GetAwaiter().GetResult();

    // Async execution
    public async Task<IResponse> AsResponseAsync()
    {
        var url = BuildUrl();
        var response = await _client.ExecuteAsync(this, url, _method, _headers, _body, _options, _retryConfig,
            _authScheme, _authParameter, _filters, _cancellationToken);
        return response;
    }

    public async Task<string> AsStringAsync() => (await AsResponseAsync()).AsString();
    public async Task<JsonDocument> AsJsonAsync() => (await AsResponseAsync()).AsJson();
    public async Task<T> AsAsync<T>() => (await AsResponseAsync()).As<T>();
    public async Task<List<T>> AsArrayAsync<T>() => (await AsResponseAsync()).AsArray<T>();

    public async Task DownloadToAsync(string path, IProgress<(long downloaded, long total)>? progress = null, CancellationToken ct = default)
    {
        var response = await AsResponseAsync();
        await response.SaveToFileAsync(path, progress, ct);
    }

    private string BuildUrl()
    {
        string url;

        // If resource is already an absolute URL, use it directly
        if (_resource.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            _resource.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = _resource;
        }
        else
        {
            var baseUrl = _client.BaseUrl.TrimEnd('/');
            var resource = _resource.TrimStart('/');
            url = string.IsNullOrEmpty(baseUrl) ? resource : $"{baseUrl}/{resource}";
        }

        if (_queryParams.Count > 0)
        {
            var query = string.Join("&", _queryParams.Select(p =>
                $"{HttpUtility.UrlEncode(p.Key)}={HttpUtility.UrlEncode(p.Value)}"));
            url = url.Contains('?') ? $"{url}&{query}" : $"{url}?{query}";
        }
        return url;
    }
}
