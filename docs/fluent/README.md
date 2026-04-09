# Fluent HTTP Client API

A modern, fluent HTTP client library for C#/.NET built as part of Modular. Provides a chainable API for HTTP operations with middleware support, retry policies, and rate limiting.

## Features

- **Fluent Interface** - Chain methods for readable request building
- **Full Async/Await** - Async throughout using `Task<T>`
- **Middleware Filters** - Request/response interception pipeline
- **Rate Limiting** - NexusMods-compliant rate limit tracking
- **Retry Policies** - Configurable retry with exponential backoff
- **Type-Safe Responses** - Generic deserialization methods

## Quick Start

```csharp
using Modular.FluentHttp.Implementation;

// Create a client
var client = FluentClientFactory.Create("https://api.example.com");

// Make a GET request
var response = await client
    .GetAsync("/v1/users/validate.json")
    .WithHeader("apikey", apiKey)
    .WithHeader("accept", "application/json")
    .SendAsync();

// Deserialize response
var user = await response.AsJsonAsync<UserInfo>();
```

## Request Building

### GET with Query Parameters

```csharp
var response = await client
    .GetAsync("/api/search")
    .WithQueryParam("q", "skyrim")
    .WithQueryParam("page", "1")
    .SendAsync();
```

### POST with JSON Body

```csharp
var response = await client
    .PostAsync("/api/endpoint")
    .WithHeader("Content-Type", "application/json")
    .WithJsonBody(new { key = "value" })
    .SendAsync();
```

### Headers and Authentication

```csharp
// Bearer token
var client = FluentClientFactory.Create("https://api.example.com")
    .SetBearerAuth(token);

// Custom headers per request
var response = await client
    .GetAsync("/api/data")
    .WithHeader("X-Custom-Header", "value")
    .SendAsync();
```

### Download with Progress

```csharp
await client
    .GetAsync("/files/download/123")
    .WithProgress((downloaded, total) =>
        Console.WriteLine($"{downloaded}/{total}"))
    .DownloadAsync("/path/to/file.zip");
```

## Client Configuration

```csharp
var client = FluentClientFactory.Create("https://api.example.com")
    .SetUserAgent("Modular/1.0")
    .SetBearerAuth(token)
    .SetRetryPolicy(maxRetries: 3, initialDelayMs: 1000)
    .SetTimeout(TimeSpan.FromSeconds(30))
    .SetRateLimiter(rateLimiter);
```

## Filters (Middleware)

Filters intercept requests and responses for cross-cutting concerns:

```csharp
// Add custom filter
client.AddFilter(new LoggingFilter(logger));

// Built-in filters
client.AddFilter(new AuthenticationFilter(apiKey));
client.AddFilter(new RateLimitFilter(rateLimiter));
```

### Filter Priority

Filters execute in priority order (lowest first for requests, highest first for responses):

| Priority Range | Purpose | Examples |
|----------------|---------|----------|
| 0-99 | Diagnostic/Debug | Timing, Tracing |
| 100-199 | Logging | Request/Response logging |
| 200-299 | Authentication | Token injection, refresh |
| 300-399 | Caching | Response caching |
| 500-599 | Rate Limiting | API rate limit handling |
| 9000+ | Error Handling | Exception throwing |

## Retry Policies

```csharp
// Configure retry on the client
var client = FluentClientFactory.Create("https://api.example.com")
    .SetRetryPolicy(maxRetries: 3, initialDelayMs: 1000);
```

Retry behavior:
- Retries on 5xx server errors and request timeouts
- Exponential backoff: 1s, 2s, 4s, ...
- Respects `Retry-After` headers on 429 responses

## Error Handling

```csharp
try
{
    var response = await client.GetAsync("/api/data").SendAsync();
}
catch (RateLimitException ex)
{
    Console.WriteLine($"Rate limited, retry after: {ex.RetryAfter}s");
}
catch (ApiException ex)
{
    Console.WriteLine($"API error {ex.StatusCode}: {ex.Message}");
}
```

## Source Code

The implementation lives in `src/Modular.FluentHttp/`:

| Directory | Contents |
|-----------|----------|
| `Interfaces/` | `IFluentClient`, `IRequest`, `IResponse`, `IHttpFilter`, `IRetryConfig` |
| `Implementation/` | `FluentClient`, `FluentRequest`, `FluentResponse`, `RequestOptions` |
| `Filters/` | Built-in middleware filters |
| `Retry/` | Retry policy implementations |

See also: [INTERFACES.md](INTERFACES.md) for the full interface reference.
