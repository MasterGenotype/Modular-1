# Fluent HTTP Client Interfaces

## Architecture Overview

The fluent interfaces provide a modern, chainable API for HTTP operations within Modular.

```
┌─────────────────────────────────────────────────────────────────┐
│                        IFluentClient                            │
│  Entry point for creating requests and setting client defaults  │
└─────────────────────────────────────────────────────────────────┘
                               │
                               │ creates
                               ▼
┌─────────────────────────────────────────────────────────────────┐
│                          IRequest                               │
│  Fluent builder for configuring individual HTTP requests        │
│  Methods return IRequest for chaining                          │
└─────────────────────────────────────────────────────────────────┘
                               │
                               │ executes → returns
                               ▼
┌─────────────────────────────────────────────────────────────────┐
│                          IResponse                              │
│  Wrapper for HTTP responses with parsing methods               │
└─────────────────────────────────────────────────────────────────┘

Supporting Interfaces:
┌─────────────┐  ┌───────────────┐
│ IHttpFilter │  │ IRetryConfig  │
│ Middleware  │  │ Retry Policy  │
└─────────────┘  └───────────────┘
```

## Interface Reference

| Interface | Location | Purpose |
|-----------|----------|---------|
| `IFluentClient` | `Interfaces/IFluentClient.cs` | Main entry point for creating requests |
| `IRequest` | `Interfaces/IRequest.cs` | Fluent request builder |
| `IResponse` | `Interfaces/IResponse.cs` | Response wrapper with deserialization |
| `IHttpFilter` | `Interfaces/IHttpFilter.cs` | Middleware for request/response interception |
| `IRetryConfig` | `Interfaces/IRetryConfig.cs` | Retry policy configuration |

## Usage Examples

### Basic GET Request

```csharp
using Modular.FluentHttp.Implementation;

var client = FluentClientFactory.Create("https://api.nexusmods.com");

var response = await client
    .GetAsync("/v1/user/tracked_mods.json")
    .WithHeader("apikey", apiKey)
    .SendAsync();

var mods = await response.AsJsonAsync<List<TrackedMod>>();
```

### POST with JSON Body

```csharp
var response = await client
    .PostAsync("/v1/endpoint")
    .WithJsonBody(new { name = "Test", version = "1.0" })
    .SendAsync();
```

### Error Handling

```csharp
var response = await client
    .GetAsync("/v1/mods/12345")
    .SendAsync();

if (!response.IsSuccessStatusCode)
{
    var error = await response.AsStringAsync();
    Console.Error.WriteLine($"Error: {error}");
}
```

### File Download with Progress

```csharp
await client
    .GetAsync("/v1/games/skyrim/mods/12345/files/67890/download")
    .WithProgress((downloaded, total) =>
    {
        int percent = total > 0 ? (int)(downloaded * 100 / total) : 0;
        Console.Write($"\rDownloading: {percent}%");
    })
    .DownloadAsync("/tmp/mod.zip");
```

## Filter System

Filters are middleware that intercept requests and responses. They implement `IHttpFilter`:

```csharp
public interface IHttpFilter
{
    string Name { get; }
    int Priority { get; }
    Task OnRequestAsync(IRequest request);
    Task OnResponseAsync(IResponse response);
}
```

### Filter Priority Conventions

| Priority Range | Purpose | Examples |
|----------------|---------|----------|
| 0-99 | Diagnostic/Debug | Timing, Tracing |
| 100-199 | Logging | Request/Response logging |
| 200-299 | Authentication | Token injection, refresh |
| 300-399 | Caching | Response caching |
| 400-499 | Transformation | Compression, encoding |
| 500-599 | Rate Limiting | API rate limit handling |
| 1000 | Default | User-defined filters |
| 9000-9999 | Error Handling | Exception throwing |

### Creating a Custom Filter

```csharp
public class MyFilter : IHttpFilter
{
    public string Name => "MyFilter";
    public int Priority => 500;

    public Task OnRequestAsync(IRequest request)
    {
        request.WithHeader("X-Custom", "value");
        return Task.CompletedTask;
    }

    public Task OnResponseAsync(IResponse response)
    {
        // Process response
        return Task.CompletedTask;
    }
}

client.AddFilter(new MyFilter());
```

## Source Code

All interfaces are defined in `src/Modular.FluentHttp/Interfaces/`. Implementations are in `src/Modular.FluentHttp/Implementation/`.
