#pragma once

#include "Types.h"
#include <chrono>

namespace modular::fluent {

/// Interface for configuring retry strategies.
/// Multiple retry configs can be chained; each gets a chance to retry.
/// Mirrors FluentHttpClient's IRetryConfig interface.
class IRetryConfig {
public:
    virtual ~IRetryConfig() = default;

    /// Maximum number of times this config will retry a request
    virtual int maxRetries() const = 0;

    /// Determine whether a failed response should be retried
    /// @param statusCode HTTP status code (or 0 for network failures)
    /// @param isTimeout Whether the failure was a timeout
    /// @return true if the request should be retried
    virtual bool shouldRetry(int statusCode, bool isTimeout) const = 0;

    /// Calculate the delay before the next retry attempt
    /// @param attempt The retry attempt number (1 = first retry)
    /// @param statusCode HTTP status code (or 0 for network failures)
    /// @return Duration to wait before retrying
    virtual std::chrono::milliseconds getDelay(int attempt, int statusCode) const = 0;

    /// Get a human-readable name for this retry config (for logging)
    virtual std::string name() const { return "IRetryConfig"; }
};

/// Shared pointer type for retry configs
using RetryConfigPtr = std::shared_ptr<IRetryConfig>;

//=============================================================================
// Common Retry Configurations
//=============================================================================

/// Retry config that retries on server errors (5xx) with exponential backoff
class ServerErrorRetryConfig : public IRetryConfig {
public:
    explicit ServerErrorRetryConfig(
        int maxRetries = 3,
        std::chrono::milliseconds initialDelay = std::chrono::milliseconds{1000},
        std::chrono::milliseconds maxDelay = std::chrono::milliseconds{16000}
    )
        : maxRetries_(maxRetries)
        , initialDelay_(initialDelay)
        , maxDelay_(maxDelay)
    {}

    int maxRetries() const override { return maxRetries_; }

    bool shouldRetry(int statusCode, bool isTimeout) const override {
        // Retry on 5xx server errors or timeouts
        return isTimeout || (statusCode >= 500 && statusCode < 600);
    }

    std::chrono::milliseconds getDelay(int attempt, int /*statusCode*/) const override {
        // Exponential backoff: 1s, 2s, 4s, 8s, ...
        auto delay = initialDelay_ * (1 << (attempt - 1));
        return std::min(delay, maxDelay_);
    }

    std::string name() const override { return "ServerErrorRetryConfig"; }

private:
    int maxRetries_;
    std::chrono::milliseconds initialDelay_;
    std::chrono::milliseconds maxDelay_;
};

/// Retry config specifically for rate limit responses (HTTP 429)
/// Respects Retry-After header when available
class RateLimitRetryConfig : public IRetryConfig {
public:
    explicit RateLimitRetryConfig(int maxRetries = 1)
        : maxRetries_(maxRetries) {}

    int maxRetries() const override { return maxRetries_; }

    bool shouldRetry(int statusCode, bool /*isTimeout*/) const override {
        return statusCode == 429;
    }

    std::chrono::milliseconds getDelay(int /*attempt*/, int /*statusCode*/) const override {
        // Default delay; actual implementation should parse Retry-After header
        // This will be overridden by the coordinator with actual header value
        return std::chrono::seconds{60};
    }

    std::string name() const override { return "RateLimitRetryConfig"; }

private:
    int maxRetries_;
};

/// Retry config for network timeouts only
class TimeoutRetryConfig : public IRetryConfig {
public:
    explicit TimeoutRetryConfig(
        int maxRetries = 2,
        std::chrono::milliseconds delay = std::chrono::milliseconds{1000}
    )
        : maxRetries_(maxRetries)
        , delay_(delay)
    {}

    int maxRetries() const override { return maxRetries_; }

    bool shouldRetry(int /*statusCode*/, bool isTimeout) const override {
        return isTimeout;
    }

    std::chrono::milliseconds getDelay(int /*attempt*/, int /*statusCode*/) const override {
        return delay_;
    }

    std::string name() const override { return "TimeoutRetryConfig"; }

private:
    int maxRetries_;
    std::chrono::milliseconds delay_;
};

} // namespace modular::fluent
