# Fluent HTTP Client Interfaces

## Architecture Overview

The fluent interfaces provide a modern, chainable API for HTTP operations while
maintaining compatibility with Modular's requirements.

```
┌─────────────────────────────────────────────────────────────────┐
│                        IFluentClient                             │
│  Entry point for creating requests and setting client defaults   │
└─────────────────────────────────────────────────────────────────┘
                               │
                               │ creates
                               ▼
┌─────────────────────────────────────────────────────────────────┐
│                          IRequest                                │
│  Fluent builder for configuring individual HTTP requests         │
│  Methods return IRequest& for chaining                          │
└─────────────────────────────────────────────────────────────────┘
                               │
                               │ executes → returns
                               ▼
┌─────────────────────────────────────────────────────────────────┐
│                          IResponse                               │
│  Wrapper for HTTP responses with parsing methods                 │
└─────────────────────────────────────────────────────────────────┘

Supporting Interfaces:
┌─────────────┐  ┌──────────────────┐  ┌───────────────┐
│ IHttpFilter │  │ IRequestCoord.   │  │ IRateLimiter  │
│ Middleware  │  │ Retry/Dispatch   │  │ API Limits    │
└─────────────┘  └──────────────────┘  └───────────────┘
```

## Interface Mapping

| FluentHttpClient (C#) | Modular Fluent (C++) | Notes |
|----------------------|---------------------|-------|
| IClient              | IFluentClient       | Main entry point |
| IRequest             | IRequest            | Request builder |
| IResponse            | IResponse           | Response wrapper |
| IHttpFilter          | IHttpFilter         | Middleware |
| IRetryConfig         | IRetryConfig        | Retry policy |
| IRequestCoordinator  | IRequestCoordinator | Dispatch control |
| IBodyBuilder         | IBodyBuilder        | Body construction |
| (none)               | IRateLimiter        | Modular-specific |

## Usage Examples

### Basic GET Request

```cpp
#include <fluent/Fluent.h>
using namespace modular::fluent;

auto client = createFluentClient("https://api.nexusmods.com");
client->setBearerAuth(apiKey);

auto mods = client->getAsync("v1/user/tracked_mods")
    ->withArgument("game_domain", "skyrimspecialedition")
    .as<std::vector<TrackedMod>>();
```

### POST with JSON Body

```cpp
NewMod mod{.name = "Test", .version = "1.0"};

auto result = client->postAsync("v1/mods", mod)
    ->as<ModResponse>();
```

### Custom Error Handling

```cpp
auto response = client->getAsync("v1/mods/12345")
    ->withIgnoreHttpErrors(true)
    .asResponse();

if (!response->isSuccessStatusCode()) {
    auto error = response->asJson();
    std::cerr << "Error: " << error["message"] << std::endl;
}
```

### File Download with Progress

```cpp
client->getAsync("v1/games/skyrim/mods/12345/files/67890/download")
    ->downloadTo("/tmp/mod.zip", [](size_t downloaded, size_t total) {
        int percent = total > 0 ? (downloaded * 100 / total) : 0;
        std::cout << "\rDownloading: " << percent << "%" << std::flush;
    });
```

## Filter System

Filters are middleware that intercept requests and responses:

### Filter Priority Conventions

| Priority Range | Purpose | Examples |
|---------------|---------|----------|
| 0-99 | Diagnostic/Debug | Timing, Tracing |
| 100-199 | Logging | Request/Response logging |
| 200-299 | Authentication | Token injection, refresh |
| 300-399 | Caching | Response caching |
| 400-499 | Transformation | Compression, encoding |
| 500-599 | Rate Limiting | API rate limit handling |
| 1000 | Default | User-defined filters |
| 9000-9999 | Error Handling | Exception throwing |

### Creating a Custom Filter

```cpp
class MyFilter : public IHttpFilter {
public:
    void onRequest(IRequest& request) override {
        request.withHeader("X-Custom", "value");
    }

    void onResponse(IResponse& response, bool httpErrorAsException) override {
        // Process response
    }

    std::string name() const override { return "MyFilter"; }
    int priority() const override { return 500; }
};

client->addFilter(std::make_shared<MyFilter>());
```

## Implementation Guidelines

When implementing these interfaces:

1. **Thread Safety**: All public methods should be thread-safe
2. **Exception Safety**: Use RAII, don't leak on exceptions
3. **Move Semantics**: Prefer moves over copies for efficiency
4. **Cancellation**: Respect stop_token for long operations
5. **Logging**: Use ILogger when available for debugging
