#pragma once

#include "Types.h"
#include <stdexcept>
#include <string>
#include <memory>

namespace modular::fluent {

//=============================================================================
// Base Exception
//=============================================================================

/// Base exception for all fluent HTTP client errors
class FluentException : public std::runtime_error {
public:
    explicit FluentException(const std::string& message)
        : std::runtime_error(message) {}

    explicit FluentException(const std::string& message, std::exception_ptr cause)
        : std::runtime_error(message), cause_(cause) {}

    /// Get the underlying cause, if any
    std::exception_ptr cause() const noexcept { return cause_; }

private:
    std::exception_ptr cause_;
};

//=============================================================================
// Network Exceptions
//=============================================================================

/// Exception for network-level failures (DNS, connection, timeout)
class NetworkException : public FluentException {
public:
    enum class Reason {
        ConnectionFailed,
        DnsResolutionFailed,
        Timeout,
        SslError,
        Unknown
    };

    NetworkException(const std::string& message, Reason reason)
        : FluentException(message), reason_(reason) {}

    Reason reason() const noexcept { return reason_; }

    bool isTimeout() const noexcept { return reason_ == Reason::Timeout; }

private:
    Reason reason_;
};

//=============================================================================
// API Exceptions (HTTP-level errors)
//=============================================================================

/// Exception for HTTP error responses (4xx, 5xx)
/// Mirrors FluentHttpClient's ApiException
class ApiException : public FluentException {
public:
    ApiException(
        const std::string& message,
        int statusCode,
        std::string statusReason,
        Headers responseHeaders,
        std::string responseBody
    )
        : FluentException(message)
        , statusCode_(statusCode)
        , statusReason_(std::move(statusReason))
        , responseHeaders_(std::move(responseHeaders))
        , responseBody_(std::move(responseBody))
    {}

    /// HTTP status code (e.g., 404, 500)
    int statusCode() const noexcept { return statusCode_; }

    /// HTTP status reason phrase (e.g., "Not Found")
    const std::string& statusReason() const noexcept { return statusReason_; }

    /// Response headers from the failed request
    const Headers& responseHeaders() const noexcept { return responseHeaders_; }

    /// Response body content (may be empty)
    const std::string& responseBody() const noexcept { return responseBody_; }

    /// Check if this is a client error (4xx)
    bool isClientError() const noexcept {
        return statusCode_ >= 400 && statusCode_ < 500;
    }

    /// Check if this is a server error (5xx)
    bool isServerError() const noexcept {
        return statusCode_ >= 500;
    }

private:
    int statusCode_;
    std::string statusReason_;
    Headers responseHeaders_;
    std::string responseBody_;
};

//=============================================================================
// Specialized API Exceptions
//=============================================================================

/// Exception for rate limit exceeded (HTTP 429)
/// Includes retry timing information from response headers
class RateLimitException : public ApiException {
public:
    RateLimitException(
        const std::string& message,
        Headers responseHeaders,
        std::string responseBody,
        std::chrono::seconds retryAfter
    )
        : ApiException(message, 429, "Too Many Requests",
                       std::move(responseHeaders), std::move(responseBody))
        , retryAfter_(retryAfter)
    {}

    /// Suggested time to wait before retrying
    std::chrono::seconds retryAfter() const noexcept { return retryAfter_; }

private:
    std::chrono::seconds retryAfter_;
};

/// Exception for authentication failures (HTTP 401, 403)
class AuthException : public ApiException {
public:
    enum class Reason {
        Unauthorized,    // 401 - Missing or invalid credentials
        Forbidden        // 403 - Valid credentials but insufficient permissions
    };

    AuthException(
        const std::string& message,
        int statusCode,
        Headers responseHeaders,
        std::string responseBody
    )
        : ApiException(message, statusCode,
                       statusCode == 401 ? "Unauthorized" : "Forbidden",
                       std::move(responseHeaders), std::move(responseBody))
        , reason_(statusCode == 401 ? Reason::Unauthorized : Reason::Forbidden)
    {}

    Reason reason() const noexcept { return reason_; }

private:
    Reason reason_;
};

//=============================================================================
// Parse/Serialization Exceptions
//=============================================================================

/// Exception for JSON/response parsing failures
class ParseException : public FluentException {
public:
    ParseException(const std::string& message, const std::string& content)
        : FluentException(message), content_(content) {}

    /// The content that failed to parse
    const std::string& content() const noexcept { return content_; }

private:
    std::string content_;
};

//=============================================================================
// Configuration Exceptions
//=============================================================================

/// Exception for invalid configuration or setup errors
class ConfigurationException : public FluentException {
public:
    explicit ConfigurationException(const std::string& message)
        : FluentException(message) {}
};

} // namespace modular::fluent
