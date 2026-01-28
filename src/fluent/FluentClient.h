#pragma once

#include <fluent/IFluentClient.h>
#include <fluent/IHttpFilter.h>
#include <fluent/IRateLimiter.h>
#include <core/ILogger.h>

#include <memory>
#include <vector>

namespace modular::fluent {

// Forward declarations
class HttpClientBridge;

/// Concrete implementation of IFluentClient
/// Main entry point for the fluent HTTP client API
class FluentClient : public IFluentClient {
public:
    /// Construct with base URL
    explicit FluentClient(std::string_view baseUrl = "");

    /// Construct with base URL and dependencies
    FluentClient(
        std::string_view baseUrl,
        RateLimiterPtr rateLimiter,
        std::shared_ptr<modular::ILogger> logger = nullptr
    );

    ~FluentClient() override;

    // Non-copyable, movable
    FluentClient(const FluentClient&) = delete;
    FluentClient& operator=(const FluentClient&) = delete;
    FluentClient(FluentClient&&) noexcept;
    FluentClient& operator=(FluentClient&&) noexcept;

    //=========================================================================
    // IFluentClient Implementation - HTTP Methods
    //=========================================================================

    RequestPtr getAsync(std::string_view resource) override;
    RequestPtr postAsync(std::string_view resource) override;
    RequestPtr putAsync(std::string_view resource) override;
    RequestPtr patchAsync(std::string_view resource) override;
    RequestPtr deleteAsync(std::string_view resource) override;
    RequestPtr headAsync(std::string_view resource) override;
    RequestPtr sendAsync(HttpMethod method, std::string_view resource) override;

    //=========================================================================
    // IFluentClient Implementation - Configuration
    //=========================================================================

    IFluentClient& setBaseUrl(std::string_view baseUrl) override;
    std::string baseUrl() const override;

    IFluentClient& setOptions(const RequestOptions& options) override;
    const RequestOptions& options() const override;

    IFluentClient& setUserAgent(std::string_view userAgent) override;

    //=========================================================================
    // IFluentClient Implementation - Authentication
    //=========================================================================

    IFluentClient& setAuthentication(
        std::string_view scheme,
        std::string_view parameter
    ) override;
    IFluentClient& setBearerAuth(std::string_view token) override;
    IFluentClient& setBasicAuth(
        std::string_view username,
        std::string_view password
    ) override;
    IFluentClient& clearAuthentication() override;

    //=========================================================================
    // IFluentClient Implementation - Filters
    //=========================================================================

    FilterCollection& filters() override;
    const FilterCollection& filters() const override;

    //=========================================================================
    // IFluentClient Implementation - Retry
    //=========================================================================

    IFluentClient& setRequestCoordinator(CoordinatorPtr coordinator) override;
    IFluentClient& setRetryPolicy(
        int maxRetries,
        std::function<bool(int statusCode, bool isTimeout)> shouldRetry,
        std::function<std::chrono::milliseconds(int attempt)> getDelay
    ) override;
    IFluentClient& setRetryPolicy(
        std::vector<std::shared_ptr<IRetryConfig>> configs
    ) override;
    IFluentClient& disableRetries() override;
    CoordinatorPtr requestCoordinator() const override;

    //=========================================================================
    // IFluentClient Implementation - Rate Limiting
    //=========================================================================

    IFluentClient& setRateLimiter(RateLimiterPtr rateLimiter) override;
    RateLimiterPtr rateLimiter() const override;

    //=========================================================================
    // IFluentClient Implementation - Defaults
    //=========================================================================

    IFluentClient& addDefault(RequestCustomizer configure) override;
    IFluentClient& clearDefaults() override;

    //=========================================================================
    // IFluentClient Implementation - Timeouts
    //=========================================================================

    IFluentClient& setConnectionTimeout(std::chrono::seconds timeout) override;
    IFluentClient& setRequestTimeout(std::chrono::seconds timeout) override;

    //=========================================================================
    // IFluentClient Implementation - Logging
    //=========================================================================

    IFluentClient& setLogger(std::shared_ptr<modular::ILogger> logger) override;

    //=========================================================================
    // Internal API (for Request class)
    //=========================================================================

    /// Get the HTTP client bridge for executing requests
    HttpClientBridge& httpClientBridge();

    /// Get default headers to apply to requests
    const Headers& defaultHeaders() const;

    /// Get default request customizers
    const std::vector<RequestCustomizer>& defaultCustomizers() const;

    /// Get the logger
    modular::ILogger* logger() const;

private:
    std::string baseUrl_;
    Headers defaultHeaders_;
    RequestOptions defaultOptions_;

    FilterCollection filters_;
    CoordinatorPtr coordinator_;
    RateLimiterPtr rateLimiter_;

    std::vector<RequestCustomizer> defaultCustomizers_;

    std::chrono::seconds connectionTimeout_{30};
    std::chrono::seconds requestTimeout_{60};

    std::shared_ptr<modular::ILogger> logger_;
    std::unique_ptr<HttpClientBridge> httpBridge_;

    /// Initialize the HTTP client bridge
    void initHttpBridge();
};

//=============================================================================
// Factory Function Implementation
//=============================================================================

inline ClientPtr createFluentClient(std::string_view baseUrl) {
    return std::make_unique<FluentClient>(baseUrl);
}

inline ClientPtr createFluentClient(
    std::string_view baseUrl,
    RateLimiterPtr rateLimiter,
    std::shared_ptr<modular::ILogger> logger
) {
    return std::make_unique<FluentClient>(baseUrl, std::move(rateLimiter), std::move(logger));
}

} // namespace modular::fluent
