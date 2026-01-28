#pragma once

#include "Types.h"
#include "IRequest.h"
#include "IResponse.h"
#include "IHttpFilter.h"
#include "IRetryConfig.h"
#include "IRequestCoordinator.h"
#include "IRateLimiter.h"
#include <core/ILogger.h>

namespace modular::fluent {

/// Main interface for the fluent HTTP client.
/// Entry point for creating and executing HTTP requests.
/// Mirrors FluentHttpClient's IClient interface.
///
/// Example usage:
/// @code
/// auto client = createFluentClient("https://api.nexusmods.com");
/// client->setBearerAuth(apiKey);
/// client->setRateLimiter(rateLimiter);
///
/// auto mods = client->getAsync("v1/user/tracked_mods")
///     ->withArgument("game_domain", "skyrimspecialedition")
///     .as<std::vector<TrackedMod>>();
/// @endcode
class IFluentClient {
public:
    virtual ~IFluentClient() = default;

    //=========================================================================
    // HTTP Methods - Request Builders
    //=========================================================================

    /// Create a GET request
    /// @param resource The resource path (relative to base URL)
    virtual RequestPtr getAsync(std::string_view resource) = 0;

    /// Create a POST request
    virtual RequestPtr postAsync(std::string_view resource) = 0;

    /// Create a POST request with body
    template<typename T>
    RequestPtr postAsync(std::string_view resource, const T& body) {
        auto request = postAsync(resource);
        request->withJsonBody(body);
        return request;
    }

    /// Create a PUT request
    virtual RequestPtr putAsync(std::string_view resource) = 0;

    /// Create a PUT request with body
    template<typename T>
    RequestPtr putAsync(std::string_view resource, const T& body) {
        auto request = putAsync(resource);
        request->withJsonBody(body);
        return request;
    }

    /// Create a PATCH request
    virtual RequestPtr patchAsync(std::string_view resource) = 0;

    /// Create a DELETE request
    virtual RequestPtr deleteAsync(std::string_view resource) = 0;

    /// Create a HEAD request
    virtual RequestPtr headAsync(std::string_view resource) = 0;

    /// Create a request with custom HTTP method
    virtual RequestPtr sendAsync(HttpMethod method, std::string_view resource) = 0;

    //=========================================================================
    // Client-Level Configuration
    //=========================================================================

    /// Set the base URL for all requests
    virtual IFluentClient& setBaseUrl(std::string_view baseUrl) = 0;

    /// Get the current base URL
    virtual std::string baseUrl() const = 0;

    /// Set default options for all requests
    virtual IFluentClient& setOptions(const RequestOptions& options) = 0;

    /// Get current default options
    virtual const RequestOptions& options() const = 0;

    /// Set the User-Agent header for all requests
    virtual IFluentClient& setUserAgent(std::string_view userAgent) = 0;

    //=========================================================================
    // Authentication
    //=========================================================================

    /// Set authentication for all requests
    /// @param scheme Authentication scheme (e.g., "Bearer", "Basic")
    /// @param parameter Authentication parameter/token
    virtual IFluentClient& setAuthentication(
        std::string_view scheme,
        std::string_view parameter
    ) = 0;

    /// Set Bearer token authentication for all requests
    virtual IFluentClient& setBearerAuth(std::string_view token) = 0;

    /// Set Basic authentication for all requests
    virtual IFluentClient& setBasicAuth(
        std::string_view username,
        std::string_view password
    ) = 0;

    /// Clear authentication
    virtual IFluentClient& clearAuthentication() = 0;

    //=========================================================================
    // Filters
    //=========================================================================

    /// Get the filter collection for this client
    virtual FilterCollection& filters() = 0;
    virtual const FilterCollection& filters() const = 0;

    /// Add a filter (convenience method)
    IFluentClient& addFilter(FilterPtr filter) {
        filters().add(std::move(filter));
        return *this;
    }

    /// Remove all filters of a type (convenience method)
    template<typename T>
    IFluentClient& removeFilters() {
        filters().removeAll<T>();
        return *this;
    }

    //=========================================================================
    // Retry and Coordination
    //=========================================================================

    /// Set the request coordinator (controls retry and dispatch)
    virtual IFluentClient& setRequestCoordinator(CoordinatorPtr coordinator) = 0;

    /// Set up simple retry with parameters
    virtual IFluentClient& setRetryPolicy(
        int maxRetries,
        std::function<bool(int statusCode, bool isTimeout)> shouldRetry,
        std::function<std::chrono::milliseconds(int attempt)> getDelay
    ) = 0;

    /// Set up retry with config objects
    virtual IFluentClient& setRetryPolicy(
        std::vector<std::shared_ptr<IRetryConfig>> configs
    ) = 0;

    /// Disable all retries
    virtual IFluentClient& disableRetries() = 0;

    /// Get the current coordinator (may be null)
    virtual CoordinatorPtr requestCoordinator() const = 0;

    //=========================================================================
    // Rate Limiting (Modular-Specific)
    //=========================================================================

    /// Set the rate limiter for this client
    virtual IFluentClient& setRateLimiter(RateLimiterPtr rateLimiter) = 0;

    /// Get the current rate limiter (may be null)
    virtual RateLimiterPtr rateLimiter() const = 0;

    //=========================================================================
    // Default Request Configuration
    //=========================================================================

    /// Add a default configuration that applies to all requests
    /// @param configure Function that configures each request
    ///
    /// Example:
    /// @code
    /// client->addDefault([](IRequest& req) {
    ///     req.withHeader("X-Client-Version", "1.0");
    /// });
    /// @endcode
    virtual IFluentClient& addDefault(RequestCustomizer configure) = 0;

    /// Clear all default configurations
    virtual IFluentClient& clearDefaults() = 0;

    //=========================================================================
    // Timeout Configuration
    //=========================================================================

    /// Set default connection timeout
    virtual IFluentClient& setConnectionTimeout(std::chrono::seconds timeout) = 0;

    /// Set default request timeout
    virtual IFluentClient& setRequestTimeout(std::chrono::seconds timeout) = 0;

    //=========================================================================
    // Logging Integration
    //=========================================================================

    /// Set logger for this client (optional)
    /// If not set, logging is disabled
    virtual IFluentClient& setLogger(std::shared_ptr<modular::ILogger> logger) = 0;
};

/// Unique pointer type for client objects
using ClientPtr = std::unique_ptr<IFluentClient>;

//=============================================================================
// Factory Function
//=============================================================================

/// Create a new FluentClient instance
/// @param baseUrl The base URL for all requests
/// @return A new client instance
ClientPtr createFluentClient(std::string_view baseUrl = "");

/// Create a new FluentClient with custom configuration
/// @param baseUrl The base URL
/// @param rateLimiter Optional rate limiter
/// @param logger Optional logger
ClientPtr createFluentClient(
    std::string_view baseUrl,
    RateLimiterPtr rateLimiter,
    std::shared_ptr<modular::ILogger> logger = nullptr
);

} // namespace modular::fluent
