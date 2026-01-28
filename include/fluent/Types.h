#pragma once

#include <chrono>
#include <functional>
#include <map>
#include <memory>
#include <optional>
#include <string>
#include <string_view>
#include <variant>
#include <vector>

namespace modular::fluent {

//=============================================================================
// Forward Declarations
//=============================================================================
class IFluentClient;
class IRequest;
class IResponse;
class IHttpFilter;
class IBodyBuilder;
class IRetryConfig;
class IRequestCoordinator;
class IRateLimiter;

//=============================================================================
// Type Aliases
//=============================================================================

/// HTTP header collection (case-insensitive keys recommended)
using Headers = std::map<std::string, std::string>;

/// Query parameter collection
using QueryParams = std::vector<std::pair<std::string, std::string>>;

/// Progress callback: (bytesDownloaded, totalBytes) -> void
/// totalBytes may be 0 if Content-Length is unknown
using ProgressCallback = std::function<void(size_t downloaded, size_t total)>;

/// Request customization callback
using RequestCustomizer = std::function<void(IRequest&)>;

//=============================================================================
// Enumerations
//=============================================================================

/// HTTP methods supported by the fluent client
enum class HttpMethod {
    GET,
    POST,
    PUT,
    PATCH,
    DELETE,
    HEAD,
    OPTIONS
};

/// When to consider the response "complete"
enum class HttpCompletionOption {
    /// Wait for full response content to be read (default)
    ResponseContentRead,
    /// Return as soon as headers are received (for streaming)
    ResponseHeadersRead
};

/// HTTP status code categories
enum class StatusCategory {
    Informational,  // 1xx
    Success,        // 2xx
    Redirection,    // 3xx
    ClientError,    // 4xx
    ServerError     // 5xx
};

//=============================================================================
// Configuration Structures
//=============================================================================

/// Options that can be set per-request or as client defaults
struct RequestOptions {
    /// Whether HTTP error responses (4xx/5xx) should NOT throw exceptions
    std::optional<bool> ignoreHttpErrors;

    /// Whether null/empty arguments should be omitted from query string
    std::optional<bool> ignoreNullArguments;

    /// When to consider the response complete
    std::optional<HttpCompletionOption> completeWhen;

    /// Request timeout duration
    std::optional<std::chrono::seconds> timeout;
};

/// Retry policy configuration
struct RetryPolicy {
    /// Maximum number of retry attempts (0 = no retries)
    int maxRetries = 3;

    /// Initial delay before first retry
    std::chrono::milliseconds initialDelay{1000};

    /// Maximum delay between retries
    std::chrono::milliseconds maxDelay{16000};

    /// Whether to use exponential backoff (true) or fixed delay (false)
    bool exponentialBackoff = true;

    /// Jitter factor (0.0-1.0) to randomize delays
    double jitterFactor = 0.1;
};

//=============================================================================
// Utility Functions
//=============================================================================

/// Convert HttpMethod enum to string
constexpr std::string_view to_string(HttpMethod method) {
    switch (method) {
        case HttpMethod::GET:     return "GET";
        case HttpMethod::POST:    return "POST";
        case HttpMethod::PUT:     return "PUT";
        case HttpMethod::PATCH:   return "PATCH";
        case HttpMethod::DELETE:  return "DELETE";
        case HttpMethod::HEAD:    return "HEAD";
        case HttpMethod::OPTIONS: return "OPTIONS";
    }
    return "UNKNOWN";
}

/// Determine status category from HTTP status code
constexpr StatusCategory categorize_status(int statusCode) {
    if (statusCode >= 100 && statusCode < 200) return StatusCategory::Informational;
    if (statusCode >= 200 && statusCode < 300) return StatusCategory::Success;
    if (statusCode >= 300 && statusCode < 400) return StatusCategory::Redirection;
    if (statusCode >= 400 && statusCode < 500) return StatusCategory::ClientError;
    return StatusCategory::ServerError;
}

/// Check if status code indicates success (2xx)
constexpr bool is_success_status(int statusCode) {
    return statusCode >= 200 && statusCode < 300;
}

} // namespace modular::fluent
