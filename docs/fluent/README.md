# Fluent HTTP Client API

A modern, fluent HTTP client API for C++ inspired by FluentHttpClient (.NET).

## Features

- **Fluent Interface**: Chain methods for readable request building
- **Async/Await Support**: Built on `std::future` for async operations
- **Middleware Filters**: Request/response interception pipeline
- **Rate Limiting**: NexusMods-compliant rate limit tracking
- **Retry Policies**: Configurable retry with exponential backoff
- **Type-Safe**: Strongly typed request/response handling

## Quick Start

```cpp
#include <fluent/Fluent.h>

using namespace modular::fluent;

// Create a client
auto client = createFluentClient("https://api.example.com");
client->setUserAgent("MyApp/1.0");
client->setBearerAuth(apiKey);

// Make a GET request
auto response = client->getAsync("users/123")->asResponse();
auto user = response->as<User>();

// Make a POST request with JSON body
auto created = client->postAsync("users")
    ->withJsonBody(newUser)
    .asJson();

// Download a file with progress
client->getAsync("files/download")
    ->withArgument("id", fileId)
    .downloadTo("/path/to/file.zip", [](size_t done, size_t total) {
        std::cout << done << "/" << total << std::endl;
    });
```

## Request Building

### URL Arguments

```cpp
client->getAsync("search")
    ->withArgument("q", "hello")
    ->withArgument("page", 1)
    ->withArgument("limit", 10)
    .asJson();
```

### Headers

```cpp
client->getAsync("api/data")
    ->withHeader("X-Custom-Header", "value")
    ->withHeaders({{"Accept", "application/json"}})
    .asResponse();
```

### Authentication

```cpp
// Bearer token
request->withBearerAuth(token);

// Basic auth
request->withBasicAuth(username, password);

// Custom scheme
request->withAuthentication("ApiKey", key);
```

### Request Body

```cpp
// JSON body
request->withJsonBody(myObject);

// Form data
request->withFormBody({{"name", "value"}, {"other", "data"}});

// Using body builder
request->withBody([](IBodyBuilder& b) {
    return b.formUrlEncoded({{"key", "value"}});
});
```

### Options

```cpp
request
    ->withTimeout(std::chrono::seconds{30})
    ->withIgnoreHttpErrors(true)  // Don't throw on 4xx/5xx
    ->withNoRetry();              // Disable retries
```

## Response Handling

```cpp
// Get full response
auto response = request->asResponse();
int status = response->statusCode();
std::string body = response->asString();

// Parse JSON
auto json = request->asJson();

// Deserialize to type
auto user = request->as<User>();

// Download to file
request->downloadTo("/path/to/file.zip");
```

## Filters (Middleware)

Filters intercept requests and responses for cross-cutting concerns.

### Built-in Filters

```cpp
// Error handling (throws on 4xx/5xx)
client->filters().add(std::make_shared<DefaultErrorFilter>());

// Logging
client->filters().add(std::make_shared<LoggingFilter>(logger));

// Rate limiting
client->filters().add(std::make_shared<RateLimitFilter>(rateLimiter));

// Authentication
client->filters().add(AuthenticationFilter::apiKey(key));
```

### Custom Filters

```cpp
class MyFilter : public IHttpFilter {
public:
    void onRequest(IRequest& request) override {
        request.withHeader("X-Request-Id", generateId());
    }

    void onResponse(IResponse& response, bool httpErrorAsException) override {
        log("Response: " + std::to_string(response.statusCode()));
    }

    std::string name() const override { return "MyFilter"; }
    int priority() const override { return 500; }
};
```

### Filter Priority

Filters execute in priority order (lowest first for requests, highest first for responses):

- 0-99: Diagnostic filters
- 100-199: Logging filters
- 200-299: Authentication filters
- 500-599: Rate limiting filters
- 9000+: Error handling filters

## Retry Policies

```cpp
// Server error retry (5xx + timeout)
auto config = std::make_shared<ServerErrorRetryConfig>(
    3,                                  // Max retries
    std::chrono::milliseconds{1000},    // Initial delay
    std::chrono::milliseconds{16000}    // Max delay
);

request->withRetryConfig(config);

// Rate limit retry (429)
request->withRetryConfig(std::make_shared<RateLimitRetryConfig>(2));

// Timeout only
request->withRetryConfig(std::make_shared<TimeoutRetryConfig>(2));
```

## NexusMods Client

High-level client for NexusMods API:

```cpp
#include <fluent/clients/NexusModsClient.h>

auto nexus = NexusModsClient::create(apiKey, rateLimiter, logger);

// Get tracked mods
auto mods = nexus->getTrackedMods("stardewvalley");

// Get mod info
auto info = nexus->getModInfo("stardewvalley", 12345);

// Download a file
auto file = nexus->getPrimaryFile("stardewvalley", 12345);
if (file) {
    nexus->downloadFile("stardewvalley", 12345, file->fileId, 
        "/mods/download.zip", progressCallback);
}
```

## Error Handling

```cpp
try {
    auto response = client->getAsync("api/data")->asResponse();
} catch (const NetworkException& e) {
    if (e.isTimeout()) {
        // Handle timeout
    }
} catch (const RateLimitException& e) {
    auto retryAfter = e.retryAfter();
    // Wait and retry
} catch (const ApiException& e) {
    int status = e.statusCode();
    std::string body = e.responseBody();
    // Handle API error
}
```

## Thread Safety

- `IFluentClient` instances are NOT thread-safe
- Each thread should create its own client or use synchronization
- Individual requests are independent and can be executed concurrently

## Building

The fluent library is built as part of the main Modular project:

```bash
cmake --preset default
cmake --build build
```

Link against `fluent_client`:

```cmake
target_link_libraries(myapp PRIVATE fluent_client)
```
