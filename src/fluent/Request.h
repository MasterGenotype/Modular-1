#pragma once

#include <fluent/IRequest.h>
#include <fluent/IFluentClient.h>
#include "BodyBuilder.h"
#include "Utils.h"

#include <typeindex>
#include <set>

namespace modular::fluent {

// Forward declarations
class FluentClient;

/// Concrete implementation of IRequest
/// Provides fluent request building and execution
class Request : public IRequest {
public:
    /// Construct a request
    /// @param method HTTP method
    /// @param resource Resource path (relative to client base URL)
    /// @param client Parent client (for defaults, filters, HTTP execution)
    Request(
        HttpMethod method,
        std::string resource,
        FluentClient* client
    );

    ~Request() override = default;

    // Non-copyable, movable
    Request(const Request&) = delete;
    Request& operator=(const Request&) = delete;
    Request(Request&&) = default;
    Request& operator=(Request&&) = default;

    //=========================================================================
    // IRequest Implementation - Read-only
    //=========================================================================

    HttpMethod method() const override;
    std::string url() const override;
    const Headers& headers() const override;
    const RequestOptions& options() const override;

    //=========================================================================
    // IRequest Implementation - URL Arguments
    //=========================================================================

    IRequest& withArgument(std::string_view key, std::string_view value) override;
    IRequest& withArguments(
        const std::vector<std::pair<std::string, std::string>>& arguments
    ) override;
    IRequest& withArguments(
        const std::map<std::string, std::string>& arguments
    ) override;

    //=========================================================================
    // IRequest Implementation - Headers
    //=========================================================================

    IRequest& withHeader(std::string_view key, std::string_view value) override;
    IRequest& withHeaders(const Headers& headers) override;
    IRequest& withoutHeader(std::string_view key) override;

    //=========================================================================
    // IRequest Implementation - Authentication
    //=========================================================================

    IRequest& withAuthentication(
        std::string_view scheme,
        std::string_view parameter
    ) override;
    IRequest& withBearerAuth(std::string_view token) override;
    IRequest& withBasicAuth(
        std::string_view username,
        std::string_view password
    ) override;

    //=========================================================================
    // IRequest Implementation - Body
    //=========================================================================

    IRequest& withBody(std::function<RequestBody(IBodyBuilder&)> builder) override;
    IRequest& withBody(RequestBody body) override;
    IRequest& withFormBody(
        const std::vector<std::pair<std::string, std::string>>& fields
    ) override;

    //=========================================================================
    // IRequest Implementation - Options
    //=========================================================================

    IRequest& withOptions(const RequestOptions& options) override;
    IRequest& withIgnoreHttpErrors(bool ignore = true) override;
    IRequest& withTimeout(std::chrono::seconds timeout) override;
    IRequest& withCancellation(std::stop_token token) override;

    //=========================================================================
    // IRequest Implementation - Filters and Retry
    //=========================================================================

    IRequest& withFilter(FilterPtr filter) override;
    IRequest& withoutFilter(const FilterPtr& filter) override;
    IRequest& withRetryConfig(std::shared_ptr<IRetryConfig> config) override;
    IRequest& withNoRetry() override;

    //=========================================================================
    // IRequest Implementation - Custom
    //=========================================================================

    IRequest& withCustom(std::function<void(IRequest&)> customizer) override;

    //=========================================================================
    // IRequest Implementation - Execution
    //=========================================================================

    std::future<ResponsePtr> asResponseAsync() override;
    std::future<std::string> asStringAsync() override;
    std::future<nlohmann::json> asJsonAsync() override;
    std::future<void> downloadToAsync(
        const std::filesystem::path& path,
        ProgressCallback progress = nullptr
    ) override;

protected:
    void removeFiltersOfType(const std::type_info& type) override;

private:
    HttpMethod method_;
    std::string resource_;
    FluentClient* client_;

    Headers headers_;
    QueryParams queryParams_;
    RequestOptions options_;
    std::optional<RequestBody> body_;

    std::vector<FilterPtr> additionalFilters_;
    std::set<std::type_index> removedFilterTypes_;

    std::shared_ptr<IRetryConfig> retryConfig_;
    bool disableRetry_ = false;

    std::stop_token cancellationToken_;

    BodyBuilder bodyBuilder_;

    /// Build the full URL with query parameters
    std::string buildFullUrl() const;

    /// Apply request filters
    void applyRequestFilters();

    /// Apply response filters
    void applyResponseFilters(IResponse& response);

    /// Execute the request and return response
    ResponsePtr executeInternal();

    /// Execute request with streaming (for downloads)
    void executeStreamingInternal(
        const std::filesystem::path& path,
        ProgressCallback progress
    );
};

} // namespace modular::fluent
