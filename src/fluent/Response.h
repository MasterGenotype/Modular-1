#pragma once

#include <fluent/IResponse.h>
#include <fluent/Exceptions.h>
#include "Utils.h"

#include <fstream>
#include <mutex>

namespace modular::fluent {

/// Concrete implementation of IResponse
/// Wraps HTTP response data and provides parsing methods
class Response : public IResponse {
public:
    /// Construct from raw HTTP response data
    /// @param statusCode HTTP status code
    /// @param statusReason HTTP status reason phrase
    /// @param headers Response headers
    /// @param body Response body content
    /// @param effectiveUrl Final URL after redirects
    /// @param elapsed Time taken for request
    Response(
        int statusCode,
        std::string statusReason,
        Headers headers,
        std::vector<uint8_t> body,
        std::string effectiveUrl,
        std::chrono::milliseconds elapsed
    );

    /// Construct from raw HTTP response with string body
    Response(
        int statusCode,
        std::string statusReason,
        Headers headers,
        std::string body,
        std::string effectiveUrl,
        std::chrono::milliseconds elapsed
    );

    // Non-copyable but movable
    Response(const Response&) = delete;
    Response& operator=(const Response&) = delete;
    Response(Response&&) = default;
    Response& operator=(Response&&) = default;

    ~Response() override = default;

    //=========================================================================
    // IResponse Implementation - Status
    //=========================================================================

    bool isSuccessStatusCode() const override;
    int statusCode() const override;
    std::string statusReason() const override;

    //=========================================================================
    // IResponse Implementation - Headers
    //=========================================================================

    const Headers& headers() const override;
    std::string header(std::string_view name) const override;
    bool hasHeader(std::string_view name) const override;
    std::string contentType() const override;
    int64_t contentLength() const override;

    //=========================================================================
    // IResponse Implementation - Body Access (Sync)
    //=========================================================================

    std::string asString() override;
    std::vector<uint8_t> asByteArray() override;
    nlohmann::json asJson() override;

    //=========================================================================
    // IResponse Implementation - Body Access (Async)
    //=========================================================================

    std::future<std::string> asStringAsync() override;
    std::future<std::vector<uint8_t>> asByteArrayAsync() override;
    std::future<nlohmann::json> asJsonAsync() override;

    //=========================================================================
    // IResponse Implementation - File Operations
    //=========================================================================

    void saveToFile(
        const std::filesystem::path& path,
        ProgressCallback progress = nullptr
    ) override;

    std::future<void> saveToFileAsync(
        const std::filesystem::path& path,
        ProgressCallback progress = nullptr
    ) override;

    //=========================================================================
    // IResponse Implementation - Metadata
    //=========================================================================

    std::string effectiveUrl() const override;
    std::chrono::milliseconds elapsed() const override;
    bool wasRedirected() const override;

    //=========================================================================
    // Factory Methods (for internal use)
    //=========================================================================

    /// Create a Response from existing HttpClient response structure
    /// This bridges Modular's existing HttpClient with the fluent API
    static std::unique_ptr<Response> fromHttpClientResponse(
        int statusCode,
        const std::string& body,
        const std::map<std::string, std::string>& headers,
        const std::string& url,
        std::chrono::milliseconds elapsed
    );

private:
    int statusCode_;
    std::string statusReason_;
    Headers headers_;
    std::vector<uint8_t> body_;
    std::string effectiveUrl_;
    std::chrono::milliseconds elapsed_;
    std::string originalUrl_;  // For redirect detection

    // Cached parsed values
    mutable std::optional<std::string> cachedString_;
    mutable std::optional<nlohmann::json> cachedJson_;
    mutable std::mutex cacheMutex_;

    // Helper to find header case-insensitively
    std::string findHeaderValue(std::string_view name) const;
};

} // namespace modular::fluent
