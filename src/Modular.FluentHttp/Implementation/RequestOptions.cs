namespace Modular.FluentHttp.Implementation;

/// <summary>
/// Options for HTTP requests.
/// </summary>
public class RequestOptions
{
    /// <summary>
    /// Whether to ignore HTTP errors (non-2xx status codes).
    /// </summary>
    public bool IgnoreHttpErrors { get; set; }

    /// <summary>
    /// Request timeout.
    /// </summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>
    /// Whether retries are disabled for this request.
    /// </summary>
    public bool NoRetry { get; set; }

    /// <summary>
    /// Creates a copy of these options.
    /// </summary>
    public RequestOptions Clone() => new()
    {
        IgnoreHttpErrors = IgnoreHttpErrors,
        Timeout = Timeout,
        NoRetry = NoRetry
    };
}

/// <summary>
/// Represents an HTTP request body.
/// </summary>
public class RequestBody
{
    public HttpContent? Content { get; set; }
    public string? ContentType { get; set; }

    public static RequestBody Json<T>(T value)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(value);
        return new RequestBody
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            ContentType = "application/json"
        };
    }

    public static RequestBody Form(IEnumerable<KeyValuePair<string, string>> fields)
    {
        return new RequestBody
        {
            Content = new FormUrlEncodedContent(fields),
            ContentType = "application/x-www-form-urlencoded"
        };
    }

    public static RequestBody String(string content, string contentType = "text/plain")
    {
        return new RequestBody
        {
            Content = new StringContent(content, System.Text.Encoding.UTF8, contentType),
            ContentType = contentType
        };
    }
}

/// <summary>
/// Builder for creating request bodies.
/// </summary>
public interface IBodyBuilder
{
    RequestBody Json<T>(T value);
    RequestBody Form(IEnumerable<KeyValuePair<string, string>> fields);
    RequestBody String(string content, string contentType = "text/plain");
}

internal class BodyBuilder : IBodyBuilder
{
    public RequestBody Json<T>(T value) => RequestBody.Json(value);
    public RequestBody Form(IEnumerable<KeyValuePair<string, string>> fields) => RequestBody.Form(fields);
    public RequestBody String(string content, string contentType) => RequestBody.String(content, contentType);
}
