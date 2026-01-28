#pragma once

#include "Types.h"
#include "Exceptions.h"
#include <future>
#include <nlohmann/json.hpp>
#include <filesystem>

namespace modular::fluent {

/// Represents an HTTP response with methods to access and parse the body.
/// Mirrors FluentHttpClient's IResponse interface.
class IResponse {
public:
    virtual ~IResponse() = default;

    //=========================================================================
    // Status Information
    //=========================================================================

    /// Whether the HTTP response indicates success (2xx status code)
    virtual bool isSuccessStatusCode() const = 0;

    /// The HTTP status code (e.g., 200, 404, 500)
    virtual int statusCode() const = 0;

    /// The HTTP status reason phrase (e.g., "OK", "Not Found")
    virtual std::string statusReason() const = 0;

    //=========================================================================
    // Headers
    //=========================================================================

    /// Get all response headers
    virtual const Headers& headers() const = 0;

    /// Get a specific header value (empty string if not found)
    virtual std::string header(std::string_view name) const = 0;

    /// Check if a header exists
    virtual bool hasHeader(std::string_view name) const = 0;

    /// Get Content-Type header value
    virtual std::string contentType() const = 0;

    /// Get Content-Length header value (-1 if not present)
    virtual int64_t contentLength() const = 0;

    //=========================================================================
    // Body Access - Synchronous
    //=========================================================================

    /// Get the response body as a string
    /// @throws ParseException if body cannot be read
    virtual std::string asString() = 0;

    /// Get the response body as raw bytes
    virtual std::vector<uint8_t> asByteArray() = 0;

    /// Get the response body as a JSON object
    /// @throws ParseException if body is not valid JSON
    virtual nlohmann::json asJson() = 0;

    /// Deserialize the response body to a typed object
    /// @throws ParseException if deserialization fails
    template<typename T>
    T as() {
        return asJson().get<T>();
    }

    /// Deserialize the response body to an array of typed objects
    template<typename T>
    std::vector<T> asArray() {
        auto json = asJson();
        if (!json.is_array()) {
            throw ParseException("Expected JSON array", json.dump());
        }
        return json.get<std::vector<T>>();
    }

    //=========================================================================
    // Body Access - Asynchronous
    //=========================================================================

    /// Asynchronously get the response body as a string
    virtual std::future<std::string> asStringAsync() = 0;

    /// Asynchronously get the response body as raw bytes
    virtual std::future<std::vector<uint8_t>> asByteArrayAsync() = 0;

    /// Asynchronously get the response body as JSON
    virtual std::future<nlohmann::json> asJsonAsync() = 0;

    //=========================================================================
    // Stream Access (for large responses)
    //=========================================================================

    /// Save the response body to a file
    /// @param path Destination file path
    /// @param progress Optional progress callback
    /// @throws std::filesystem::filesystem_error on file I/O errors
    virtual void saveToFile(
        const std::filesystem::path& path,
        ProgressCallback progress = nullptr
    ) = 0;

    /// Asynchronously save the response body to a file
    virtual std::future<void> saveToFileAsync(
        const std::filesystem::path& path,
        ProgressCallback progress = nullptr
    ) = 0;

    //=========================================================================
    // Metadata
    //=========================================================================

    /// Get the final URL (after any redirects)
    virtual std::string effectiveUrl() const = 0;

    /// Get the total time taken for the request (including redirects)
    virtual std::chrono::milliseconds elapsed() const = 0;

    /// Check if the response was from a redirect
    virtual bool wasRedirected() const = 0;
};

/// Unique pointer type for response objects
using ResponsePtr = std::unique_ptr<IResponse>;

} // namespace modular::fluent
