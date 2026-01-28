#pragma once

#include "Types.h"
#include "IResponse.h"
#include "IBodyBuilder.h"
#include "IHttpFilter.h"
#include <future>
#include <functional>
#include <stop_token>
#include <typeinfo>

namespace modular::fluent {

// Forward declarations
class IRetryConfig;
class IRequestCoordinator;

/// Fluent interface for building and executing HTTP requests.
/// Supports method chaining and async execution.
/// Mirrors FluentHttpClient's IRequest interface.
///
/// Example usage:
/// @code
/// auto response = client->getAsync("users")
///     .withArgument("page", "1")
///     .withArgument("limit", "10")
///     .withBearerAuth(token)
///     .asResponse();
/// @endcode
class IRequest {
public:
    virtual ~IRequest() = default;

    //=========================================================================
    // Request Information (Read-only)
    //=========================================================================

    /// Get the HTTP method for this request
    virtual HttpMethod method() const = 0;

    /// Get the current URL (including any added query parameters)
    virtual std::string url() const = 0;

    /// Get the current headers
    virtual const Headers& headers() const = 0;

    /// Get the current request options
    virtual const RequestOptions& options() const = 0;

    //=========================================================================
    // URL Arguments (Query String)
    //=========================================================================

    /// Add a query string argument
    /// @param key Parameter name
    /// @param value Parameter value (will be URL-encoded)
    /// @return Reference to this request for chaining
    virtual IRequest& withArgument(std::string_view key, std::string_view value) = 0;

    /// Add a query string argument with numeric value
    template<typename T, typename = std::enable_if_t<std::is_arithmetic_v<T>>>
    IRequest& withArgument(std::string_view key, T value) {
        return withArgument(key, std::to_string(value));
    }

    /// Add multiple query string arguments
    virtual IRequest& withArguments(
        const std::vector<std::pair<std::string, std::string>>& arguments
    ) = 0;

    /// Add query string arguments from a map
    virtual IRequest& withArguments(
        const std::map<std::string, std::string>& arguments
    ) = 0;

    //=========================================================================
    // Headers
    //=========================================================================

    /// Set an HTTP header (replaces if exists)
    virtual IRequest& withHeader(std::string_view key, std::string_view value) = 0;

    /// Set multiple headers
    virtual IRequest& withHeaders(const Headers& headers) = 0;

    /// Remove a header
    virtual IRequest& withoutHeader(std::string_view key) = 0;

    //=========================================================================
    // Authentication
    //=========================================================================

    /// Set authentication header with custom scheme
    /// @param scheme Authentication scheme (e.g., "Bearer", "Basic")
    /// @param parameter Authentication parameter/token
    virtual IRequest& withAuthentication(
        std::string_view scheme,
        std::string_view parameter
    ) = 0;

    /// Set Bearer token authentication (OAuth, API keys)
    /// Equivalent to: withAuthentication("Bearer", token)
    virtual IRequest& withBearerAuth(std::string_view token) = 0;

    /// Set Basic authentication
    /// @param username The username
    /// @param password The password
    virtual IRequest& withBasicAuth(
        std::string_view username,
        std::string_view password
    ) = 0;

    //=========================================================================
    // Request Body
    //=========================================================================

    /// Set the request body using a builder function
    /// @param builder Function that constructs the body
    virtual IRequest& withBody(std::function<RequestBody(IBodyBuilder&)> builder) = 0;

    /// Set the request body directly
    virtual IRequest& withBody(RequestBody body) = 0;

    /// Set JSON body from a serializable object
    template<typename T>
    IRequest& withJsonBody(const T& value) {
        return withBody([&value](IBodyBuilder& b) {
            return b.model(value);
        });
    }

    /// Set form URL-encoded body
    virtual IRequest& withFormBody(
        const std::vector<std::pair<std::string, std::string>>& fields
    ) = 0;

    //=========================================================================
    // Options and Configuration
    //=========================================================================

    /// Set request-specific options (overrides client defaults)
    virtual IRequest& withOptions(const RequestOptions& options) = 0;

    /// Set whether HTTP errors should throw exceptions for this request
    virtual IRequest& withIgnoreHttpErrors(bool ignore = true) = 0;

    /// Set request timeout
    virtual IRequest& withTimeout(std::chrono::seconds timeout) = 0;

    /// Set cancellation token for this request
    virtual IRequest& withCancellation(std::stop_token token) = 0;

    //=========================================================================
    // Filters and Retry
    //=========================================================================

    /// Add a filter for this request only
    virtual IRequest& withFilter(FilterPtr filter) = 0;

    /// Remove a filter from this request
    virtual IRequest& withoutFilter(const FilterPtr& filter) = 0;

    /// Remove all filters of a specific type
    template<typename T>
    IRequest& withoutFilter() {
        removeFiltersOfType(typeid(T));
        return *this;
    }

    /// Set request-specific retry configuration
    virtual IRequest& withRetryConfig(std::shared_ptr<IRetryConfig> config) = 0;

    /// Disable retries for this request
    virtual IRequest& withNoRetry() = 0;

    //=========================================================================
    // Custom Modifications
    //=========================================================================

    /// Apply custom modifications to the request
    /// Use sparingly - prefer specific methods when available
    virtual IRequest& withCustom(std::function<void(IRequest&)> customizer) = 0;

    //=========================================================================
    // Response - Asynchronous (Primary API)
    //=========================================================================

    /// Execute the request and return the response asynchronously
    virtual std::future<ResponsePtr> asResponseAsync() = 0;

    /// Execute and deserialize response to typed object asynchronously
    template<typename T>
    std::future<T> asAsync() {
        return std::async(std::launch::async, [this]() {
            auto response = asResponseAsync().get();
            if (!response->isSuccessStatusCode() && !options().ignoreHttpErrors.value_or(false)) {
                // Exception will be thrown by response parsing
            }
            return response->as<T>();
        });
    }

    /// Execute and get response as string asynchronously
    virtual std::future<std::string> asStringAsync() = 0;

    /// Execute and get response as JSON asynchronously
    virtual std::future<nlohmann::json> asJsonAsync() = 0;

    /// Execute and download response to file asynchronously
    virtual std::future<void> downloadToAsync(
        const std::filesystem::path& path,
        ProgressCallback progress = nullptr
    ) = 0;

    //=========================================================================
    // Response - Synchronous (Convenience)
    //=========================================================================

    /// Execute the request and return the response (blocking)
    ResponsePtr asResponse() {
        return asResponseAsync().get();
    }

    /// Execute and deserialize response to typed object (blocking)
    template<typename T>
    T as() {
        return asAsync<T>().get();
    }

    /// Execute and get response as string (blocking)
    std::string asString() {
        return asStringAsync().get();
    }

    /// Execute and get response as JSON (blocking)
    nlohmann::json asJson() {
        return asJsonAsync().get();
    }

    /// Execute and download to file (blocking)
    void downloadTo(
        const std::filesystem::path& path,
        ProgressCallback progress = nullptr
    ) {
        return downloadToAsync(path, progress).get();
    }

protected:
    /// Helper for removing filters by type (called by template)
    virtual void removeFiltersOfType(const std::type_info& type) = 0;
};

/// Unique pointer type for request objects
using RequestPtr = std::unique_ptr<IRequest>;

} // namespace modular::fluent
