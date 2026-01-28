#include "Request.h"
#include "FluentClient.h"
#include "Response.h"
#include "HttpClientBridge.h"
#include "Utils.h"
#include <fluent/IHttpFilter.h>
#include <fluent/Exceptions.h>

#include <sstream>
#include <algorithm>
#include <fstream>
#include <future>
#include <thread>

namespace modular::fluent {

//=============================================================================
// Request Implementation
//=============================================================================

Request::Request(HttpMethod method, std::string resource, FluentClient* client)
    : method_(method)
    , resource_(std::move(resource))
    , client_(client)
{}

//-----------------------------------------------------------------------------
// Read-only Accessors
//-----------------------------------------------------------------------------

HttpMethod Request::method() const {
    return method_;
}

std::string Request::url() const {
    return buildFullUrl();
}

const Headers& Request::headers() const {
    return headers_;
}

const RequestOptions& Request::options() const {
    return options_;
}

//-----------------------------------------------------------------------------
// URL Arguments
//-----------------------------------------------------------------------------

IRequest& Request::withArgument(std::string_view key, std::string_view value) {
    queryParams_.emplace_back(std::string(key), std::string(value));
    return *this;
}

IRequest& Request::withArguments(
    const std::vector<std::pair<std::string, std::string>>& arguments
) {
    for (const auto& [key, value] : arguments) {
        queryParams_.emplace_back(key, value);
    }
    return *this;
}

IRequest& Request::withArguments(const std::map<std::string, std::string>& arguments) {
    for (const auto& [key, value] : arguments) {
        queryParams_.emplace_back(key, value);
    }
    return *this;
}

//-----------------------------------------------------------------------------
// Headers
//-----------------------------------------------------------------------------

IRequest& Request::withHeader(std::string_view key, std::string_view value) {
    headers_[std::string(key)] = std::string(value);
    return *this;
}

IRequest& Request::withHeaders(const Headers& headers) {
    for (const auto& [key, value] : headers) {
        headers_[key] = value;
    }
    return *this;
}

IRequest& Request::withoutHeader(std::string_view key) {
    headers_.erase(std::string(key));
    return *this;
}

//-----------------------------------------------------------------------------
// Authentication
//-----------------------------------------------------------------------------

IRequest& Request::withAuthentication(std::string_view scheme, std::string_view parameter) {
    return withHeader("Authorization", std::string(scheme) + " " + std::string(parameter));
}

IRequest& Request::withBearerAuth(std::string_view token) {
    return withAuthentication("Bearer", token);
}

IRequest& Request::withBasicAuth(std::string_view username, std::string_view password) {
    std::string credentials = std::string(username) + ":" + std::string(password);
    std::string encoded = detail::base64Encode(credentials);
    return withAuthentication("Basic", encoded);
}

//-----------------------------------------------------------------------------
// Body Building
//-----------------------------------------------------------------------------

IRequest& Request::withBody(std::function<RequestBody(IBodyBuilder&)> builder) {
    body_ = builder(bodyBuilder_);
    return *this;
}

IRequest& Request::withBody(RequestBody body) {
    body_ = std::move(body);
    return *this;
}

IRequest& Request::withFormBody(
    const std::vector<std::pair<std::string, std::string>>& fields
) {
    return withBody([&fields](IBodyBuilder& builder) {
        return builder.formUrlEncoded(fields);
    });
}

//-----------------------------------------------------------------------------
// Options
//-----------------------------------------------------------------------------

IRequest& Request::withOptions(const RequestOptions& options) {
    options_ = options;
    return *this;
}

IRequest& Request::withIgnoreHttpErrors(bool ignore) {
    options_.ignoreHttpErrors = ignore;
    return *this;
}

IRequest& Request::withTimeout(std::chrono::seconds timeout) {
    options_.timeout = std::make_optional(timeout);
    return *this;
}

IRequest& Request::withCancellation(std::stop_token token) {
    cancellationToken_ = std::move(token);
    return *this;
}

//-----------------------------------------------------------------------------
// Filters and Retry
//-----------------------------------------------------------------------------

IRequest& Request::withFilter(FilterPtr filter) {
    additionalFilters_.push_back(std::move(filter));
    return *this;
}

IRequest& Request::withoutFilter(const FilterPtr& filter) {
    auto it = std::find(additionalFilters_.begin(), additionalFilters_.end(), filter);
    if (it != additionalFilters_.end()) {
        additionalFilters_.erase(it);
    }
    return *this;
}

void Request::removeFiltersOfType(const std::type_info& type) {
    removedFilterTypes_.insert(std::type_index(type));
}

IRequest& Request::withRetryConfig(std::shared_ptr<IRetryConfig> config) {
    retryConfig_ = std::move(config);
    disableRetry_ = false;
    return *this;
}

IRequest& Request::withNoRetry() {
    disableRetry_ = true;
    retryConfig_ = nullptr;
    return *this;
}

//-----------------------------------------------------------------------------
// Custom
//-----------------------------------------------------------------------------

IRequest& Request::withCustom(std::function<void(IRequest&)> customizer) {
    customizer(*this);
    return *this;
}

//-----------------------------------------------------------------------------
// Execution - Async Methods
//-----------------------------------------------------------------------------

std::future<ResponsePtr> Request::asResponseAsync() {
    return std::async(std::launch::async, [this]() {
        return executeInternal();
    });
}

std::future<std::string> Request::asStringAsync() {
    return std::async(std::launch::async, [this]() {
        auto response = executeInternal();
        if (!options_.ignoreHttpErrors.value_or(false) && !response->isSuccessStatusCode()) {
            throw ApiException(
                "HTTP " + std::to_string(response->statusCode()) + ": " + response->statusReason(),
                response->statusCode(),
                response->statusReason(),
                response->headers(),
                response->asString()
            );
        }
        return response->asString();
    });
}

std::future<nlohmann::json> Request::asJsonAsync() {
    return std::async(std::launch::async, [this]() {
        auto response = executeInternal();
        if (!options_.ignoreHttpErrors.value_or(false) && !response->isSuccessStatusCode()) {
            throw ApiException(
                "HTTP " + std::to_string(response->statusCode()) + ": " + response->statusReason(),
                response->statusCode(),
                response->statusReason(),
                response->headers(),
                response->asString()
            );
        }
        return response->asJson();
    });
}

std::future<void> Request::downloadToAsync(
    const std::filesystem::path& path,
    ProgressCallback progress
) {
    return std::async(std::launch::async, [this, path, progress]() {
        executeStreamingInternal(path, progress);
    });
}

//-----------------------------------------------------------------------------
// Private Helpers
//-----------------------------------------------------------------------------

std::string Request::buildFullUrl() const {
    std::ostringstream url;
    url << client_->baseUrl();

    // Add resource path
    if (!resource_.empty()) {
        std::string base = client_->baseUrl();
        if (!base.empty() && base.back() != '/' && resource_.front() != '/') {
            url << '/';
        }
        url << resource_;
    }

    // Add query parameters
    if (!queryParams_.empty()) {
        url << '?';
        bool first = true;
        for (const auto& [key, value] : queryParams_) {
            if (!first) url << '&';
            first = false;
            url << detail::urlEncode(key) << '=' << detail::urlEncode(value);
        }
    }

    return url.str();
}

void Request::applyRequestFilters() {
    // Merge client default headers
    for (const auto& [key, value] : client_->defaultHeaders()) {
        if (headers_.find(key) == headers_.end()) {
            headers_[key] = value;
        }
    }

    // Apply default customizers from client
    for (const auto& customizer : client_->defaultCustomizers()) {
        customizer(*this);
    }

    // Get client filters and add additional filters
    std::vector<FilterPtr> allFilters;
    const auto& clientFilters = client_->filters().all();

    for (const auto& filter : clientFilters) {
        // Skip removed filter types
        if (removedFilterTypes_.count(std::type_index(typeid(*filter))) == 0) {
            allFilters.push_back(filter);
        }
    }

    for (const auto& filter : additionalFilters_) {
        allFilters.push_back(filter);
    }

    // Sort by priority (lowest first for request)
    std::sort(allFilters.begin(), allFilters.end(),
        [](const auto& a, const auto& b) {
            return a->priority() < b->priority();
        }
    );

    // Apply filters
    for (const auto& filter : allFilters) {
        filter->onRequest(*this);
    }
}

void Request::applyResponseFilters(IResponse& response) {
    // Get client filters and add additional filters
    std::vector<FilterPtr> allFilters;
    const auto& clientFilters = client_->filters().all();

    for (const auto& filter : clientFilters) {
        if (removedFilterTypes_.count(std::type_index(typeid(*filter))) == 0) {
            allFilters.push_back(filter);
        }
    }

    for (const auto& filter : additionalFilters_) {
        allFilters.push_back(filter);
    }

    // Sort by priority (highest first for response)
    std::sort(allFilters.begin(), allFilters.end(),
        [](const auto& a, const auto& b) {
            return a->priority() > b->priority();
        }
    );

    // Determine if HTTP errors should throw
    bool httpErrorAsException = !options_.ignoreHttpErrors.value_or(false);

    // Apply filters
    for (const auto& filter : allFilters) {
        filter->onResponse(response, httpErrorAsException);
    }
}

ResponsePtr Request::executeInternal() {
    applyRequestFilters();

    HttpRequestConfig config;
    config.url = buildFullUrl();
    config.method = method_;
    config.headers = headers_;
    config.timeout = options_.timeout.value_or(std::chrono::seconds{60});
    config.followRedirects = true;
    config.maxRedirects = 5;

    // Set body if present
    if (body_) {
        config.body = body_->content;
        if (!body_->contentType.empty()) {
            config.headers["Content-Type"] = body_->contentType;
        }
    }

    // Execute with retry logic if configured
    int maxAttempts = disableRetry_ ? 1 : (retryConfig_ ? retryConfig_->maxRetries() + 1 : 1);

    for (int attempt = 1; attempt <= maxAttempts; ++attempt) {
        // Check cancellation
        if (cancellationToken_.stop_requested()) {
            throw std::runtime_error("Request cancelled");
        }

        bool isTimeout = false;
        try {
            auto result = client_->httpClientBridge().execute(config);

            auto response = std::make_unique<Response>(
                result.statusCode,
                result.statusReason,
                result.headers,
                std::move(result.body),
                result.effectiveUrl,
                result.elapsed
            );

            applyResponseFilters(*response);

            // Check if we should retry based on status code
            if (retryConfig_ && !disableRetry_ &&
                retryConfig_->shouldRetry(result.statusCode, result.wasTimeout) &&
                attempt < maxAttempts) {

                auto delay = retryConfig_->getDelay(attempt, result.statusCode);

                if (client_->logger()) {
                    client_->logger()->warn("Retrying request (attempt " + std::to_string(attempt) +
                                           ") after status " + std::to_string(result.statusCode));
                }

                std::this_thread::sleep_for(delay);
                continue;
            }

            return response;

        } catch (const NetworkException& ex) {
            isTimeout = (ex.reason() == NetworkException::Reason::Timeout);

            if (retryConfig_ && !disableRetry_ &&
                retryConfig_->shouldRetry(0, isTimeout) &&
                attempt < maxAttempts) {

                auto delay = retryConfig_->getDelay(attempt, 0);

                if (client_->logger()) {
                    client_->logger()->warn("Retrying request (attempt " + std::to_string(attempt) +
                                           ") after network error: " + ex.what());
                }

                std::this_thread::sleep_for(delay);
                continue;
            }
            throw;
        }
    }

    // Should not reach here
    throw std::runtime_error("Request failed after retries");
}

void Request::executeStreamingInternal(
    const std::filesystem::path& path,
    ProgressCallback progress
) {
    applyRequestFilters();

    std::ofstream file(path, std::ios::binary);
    if (!file) {
        throw std::runtime_error("Failed to open file for writing: " + path.string());
    }

    HttpRequestConfig config;
    config.url = buildFullUrl();
    config.method = method_;
    config.headers = headers_;
    config.timeout = options_.timeout.value_or(std::chrono::seconds{60});
    config.followRedirects = true;
    config.maxRedirects = 5;

    auto result = client_->httpClientBridge().executeStreaming(
        config,
        [&file](const uint8_t* data, size_t size) {
            file.write(reinterpret_cast<const char*>(data), static_cast<std::streamsize>(size));
        },
        std::move(progress)
    );

    file.close();

    auto response = std::make_unique<Response>(
        result.statusCode,
        result.statusReason,
        result.headers,
        std::vector<uint8_t>{},
        result.effectiveUrl,
        result.elapsed
    );

    applyResponseFilters(*response);

    if (!options_.ignoreHttpErrors.value_or(false) && !response->isSuccessStatusCode()) {
        throw ApiException(
            "HTTP " + std::to_string(response->statusCode()) + ": " + response->statusReason(),
            response->statusCode(),
            response->statusReason(),
            response->headers(),
            ""
        );
    }
}

} // namespace modular::fluent
