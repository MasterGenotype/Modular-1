#pragma once

#include <fluent/Types.h>
#include <fluent/IResponse.h>
#include <fluent/Exceptions.h>
#include <core/ILogger.h>

#include <functional>
#include <optional>
#include <chrono>
#include <memory>

namespace modular::fluent {

/// Result from HTTP request execution
struct HttpResult {
    int statusCode = 0;
    std::string statusReason;
    Headers headers;
    std::vector<uint8_t> body;
    std::string effectiveUrl;
    std::chrono::milliseconds elapsed{0};
    bool wasTimeout = false;
};

/// Configuration for an HTTP request
struct HttpRequestConfig {
    HttpMethod method = HttpMethod::GET;
    std::string url;
    Headers headers;
    std::optional<std::vector<uint8_t>> body;
    std::chrono::seconds timeout{60};
    bool followRedirects = true;
    int maxRedirects = 5;
};

/// Bridge class that adapts Modular's HttpClient for the fluent API
/// Extends functionality to support all HTTP methods and streaming
class HttpClientBridge {
public:
    /// Construct with dependencies
    HttpClientBridge(
        modular::ILogger* logger = nullptr
    );

    ~HttpClientBridge();

    // Non-copyable, movable
    HttpClientBridge(const HttpClientBridge&) = delete;
    HttpClientBridge& operator=(const HttpClientBridge&) = delete;
    HttpClientBridge(HttpClientBridge&&) noexcept;
    HttpClientBridge& operator=(HttpClientBridge&&) noexcept;

    //=========================================================================
    // Core Request Methods
    //=========================================================================

    /// Execute an HTTP request
    /// @param config Request configuration
    /// @return HTTP result
    /// @throws NetworkException on network errors
    HttpResult execute(const HttpRequestConfig& config);

    /// Execute an HTTP request with streaming response
    /// @param config Request configuration
    /// @param onData Callback for each data chunk
    /// @param onProgress Progress callback (downloaded, total)
    /// @return HTTP result (body will be empty - data sent via callback)
    HttpResult executeStreaming(
        const HttpRequestConfig& config,
        std::function<void(const uint8_t*, size_t)> onData,
        ProgressCallback onProgress = nullptr
    );

    //=========================================================================
    // Configuration
    //=========================================================================

    /// Set connection timeout
    void setConnectionTimeout(std::chrono::seconds timeout);

    /// Set SSL verification
    void setSslVerification(bool verify);

    /// Set proxy
    void setProxy(const std::string& proxyUrl);

    /// Set logger
    void setLogger(modular::ILogger* logger);

private:
    class Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace modular::fluent
