#include "FluentClient.h"
#include "Request.h"
#include "HttpClientBridge.h"
#include "Utils.h"

namespace modular::fluent {

//=============================================================================
// FluentClient Implementation
//=============================================================================

FluentClient::FluentClient(std::string_view baseUrl)
    : baseUrl_(baseUrl)
{
    initHttpBridge();
}

FluentClient::FluentClient(
    std::string_view baseUrl,
    RateLimiterPtr rateLimiter,
    std::shared_ptr<modular::ILogger> logger
)
    : baseUrl_(baseUrl)
    , rateLimiter_(std::move(rateLimiter))
    , logger_(std::move(logger))
{
    initHttpBridge();
}

FluentClient::~FluentClient() = default;

FluentClient::FluentClient(FluentClient&&) noexcept = default;
FluentClient& FluentClient::operator=(FluentClient&&) noexcept = default;

void FluentClient::initHttpBridge() {
    httpBridge_ = std::make_unique<HttpClientBridge>(logger_.get());
}

//-----------------------------------------------------------------------------
// HTTP Methods
//-----------------------------------------------------------------------------

RequestPtr FluentClient::getAsync(std::string_view resource) {
    return sendAsync(HttpMethod::GET, resource);
}

RequestPtr FluentClient::postAsync(std::string_view resource) {
    return sendAsync(HttpMethod::POST, resource);
}

RequestPtr FluentClient::putAsync(std::string_view resource) {
    return sendAsync(HttpMethod::PUT, resource);
}

RequestPtr FluentClient::patchAsync(std::string_view resource) {
    return sendAsync(HttpMethod::PATCH, resource);
}

RequestPtr FluentClient::deleteAsync(std::string_view resource) {
    return sendAsync(HttpMethod::DELETE, resource);
}

RequestPtr FluentClient::headAsync(std::string_view resource) {
    return sendAsync(HttpMethod::HEAD, resource);
}

RequestPtr FluentClient::sendAsync(HttpMethod method, std::string_view resource) {
    auto request = std::make_unique<Request>(method, std::string(resource), this);

    // Apply default options
    if (defaultOptions_.timeout.has_value()) {
        request->withTimeout(defaultOptions_.timeout.value());
    }

    return request;
}

//-----------------------------------------------------------------------------
// Configuration
//-----------------------------------------------------------------------------

IFluentClient& FluentClient::setBaseUrl(std::string_view baseUrl) {
    baseUrl_ = baseUrl;
    return *this;
}

std::string FluentClient::baseUrl() const {
    return baseUrl_;
}

IFluentClient& FluentClient::setOptions(const RequestOptions& options) {
    defaultOptions_ = options;
    return *this;
}

const RequestOptions& FluentClient::options() const {
    return defaultOptions_;
}

IFluentClient& FluentClient::setUserAgent(std::string_view userAgent) {
    defaultHeaders_["User-Agent"] = std::string(userAgent);
    return *this;
}

//-----------------------------------------------------------------------------
// Authentication
//-----------------------------------------------------------------------------

IFluentClient& FluentClient::setAuthentication(std::string_view scheme, std::string_view parameter) {
    defaultHeaders_["Authorization"] = std::string(scheme) + " " + std::string(parameter);
    return *this;
}

IFluentClient& FluentClient::setBearerAuth(std::string_view token) {
    return setAuthentication("Bearer", token);
}

IFluentClient& FluentClient::setBasicAuth(std::string_view username, std::string_view password) {
    std::string credentials = std::string(username) + ":" + std::string(password);
    std::string encoded = detail::base64Encode(credentials);
    return setAuthentication("Basic", encoded);
}

IFluentClient& FluentClient::clearAuthentication() {
    defaultHeaders_.erase("Authorization");
    return *this;
}

//-----------------------------------------------------------------------------
// Filters
//-----------------------------------------------------------------------------

FilterCollection& FluentClient::filters() {
    return filters_;
}

const FilterCollection& FluentClient::filters() const {
    return filters_;
}

//-----------------------------------------------------------------------------
// Retry
//-----------------------------------------------------------------------------

IFluentClient& FluentClient::setRequestCoordinator(CoordinatorPtr coordinator) {
    coordinator_ = std::move(coordinator);
    return *this;
}

IFluentClient& FluentClient::setRetryPolicy(
    int maxRetries,
    std::function<bool(int statusCode, bool isTimeout)> shouldRetry,
    std::function<std::chrono::milliseconds(int attempt)> getDelay
) {
    // Store retry configuration (will be applied via coordinator)
    // For now, just update the coordinator if one exists
    (void)maxRetries;
    (void)shouldRetry;
    (void)getDelay;
    return *this;
}

IFluentClient& FluentClient::setRetryPolicy(
    std::vector<std::shared_ptr<IRetryConfig>> configs
) {
    // Store retry configs for use by requests
    (void)configs;
    return *this;
}

IFluentClient& FluentClient::disableRetries() {
    coordinator_ = nullptr;
    return *this;
}

CoordinatorPtr FluentClient::requestCoordinator() const {
    return coordinator_;
}

//-----------------------------------------------------------------------------
// Rate Limiting
//-----------------------------------------------------------------------------

IFluentClient& FluentClient::setRateLimiter(RateLimiterPtr rateLimiter) {
    rateLimiter_ = std::move(rateLimiter);
    return *this;
}

RateLimiterPtr FluentClient::rateLimiter() const {
    return rateLimiter_;
}

//-----------------------------------------------------------------------------
// Defaults
//-----------------------------------------------------------------------------

IFluentClient& FluentClient::addDefault(RequestCustomizer configure) {
    defaultCustomizers_.push_back(std::move(configure));
    return *this;
}

IFluentClient& FluentClient::clearDefaults() {
    defaultCustomizers_.clear();
    defaultHeaders_.clear();
    return *this;
}

//-----------------------------------------------------------------------------
// Timeouts
//-----------------------------------------------------------------------------

IFluentClient& FluentClient::setConnectionTimeout(std::chrono::seconds timeout) {
    connectionTimeout_ = timeout;
    if (httpBridge_) {
        httpBridge_->setConnectionTimeout(timeout);
    }
    return *this;
}

IFluentClient& FluentClient::setRequestTimeout(std::chrono::seconds timeout) {
    requestTimeout_ = timeout;
    defaultOptions_.timeout = std::make_optional(timeout);
    return *this;
}

//-----------------------------------------------------------------------------
// Logging
//-----------------------------------------------------------------------------

IFluentClient& FluentClient::setLogger(std::shared_ptr<modular::ILogger> logger) {
    logger_ = std::move(logger);
    if (httpBridge_) {
        httpBridge_->setLogger(logger_.get());
    }
    return *this;
}

//-----------------------------------------------------------------------------
// Internal API
//-----------------------------------------------------------------------------

HttpClientBridge& FluentClient::httpClientBridge() {
    return *httpBridge_;
}

const Headers& FluentClient::defaultHeaders() const {
    return defaultHeaders_;
}

const std::vector<RequestCustomizer>& FluentClient::defaultCustomizers() const {
    return defaultCustomizers_;
}

modular::ILogger* FluentClient::logger() const {
    return logger_.get();
}

} // namespace modular::fluent
