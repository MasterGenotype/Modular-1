#pragma once

#include "Types.h"
#include <chrono>
#include <filesystem>

namespace modular::fluent {

/// Rate limit status information
struct RateLimitStatus {
    /// Daily requests remaining
    int dailyRemaining = 0;

    /// Daily request limit
    int dailyLimit = 0;

    /// When daily limit resets
    std::chrono::system_clock::time_point dailyReset;

    /// Hourly requests remaining
    int hourlyRemaining = 0;

    /// Hourly request limit
    int hourlyLimit = 0;

    /// When hourly limit resets
    std::chrono::system_clock::time_point hourlyReset;

    /// Whether we can make a request right now
    bool canRequest() const {
        return dailyRemaining > 0 && hourlyRemaining > 0;
    }

    /// Time until next request is allowed (0 if allowed now)
    std::chrono::milliseconds timeUntilAllowed() const {
        if (canRequest()) {
            return std::chrono::milliseconds{0};
        }

        auto now = std::chrono::system_clock::now();

        if (dailyRemaining <= 0) {
            auto wait = std::chrono::duration_cast<std::chrono::milliseconds>(
                dailyReset - now);
            return wait.count() > 0 ? wait : std::chrono::milliseconds{0};
        }

        if (hourlyRemaining <= 0) {
            auto wait = std::chrono::duration_cast<std::chrono::milliseconds>(
                hourlyReset - now);
            return wait.count() > 0 ? wait : std::chrono::milliseconds{0};
        }

        return std::chrono::milliseconds{0};
    }
};

/// Interface for rate limiting HTTP requests.
/// This is a Modular-specific extension for NexusMods API compliance.
///
/// NexusMods rate limits:
/// - 2,500 requests per day (resets at midnight UTC)
/// - 100 requests per hour (once daily exhausted)
///
/// Usage:
/// @code
/// // In a filter or coordinator
/// rateLimiter->waitIfNeeded();  // Block until allowed
/// auto response = sendRequest();
/// rateLimiter->updateFromHeaders(response.headers());
/// @endcode
class IRateLimiter {
public:
    virtual ~IRateLimiter() = default;

    //=========================================================================
    // Request Control
    //=========================================================================

    /// Check if a request can be made immediately
    virtual bool canMakeRequest() const = 0;

    /// Block until a request is allowed, then return
    /// @param maxWait Maximum time to wait (0 = wait indefinitely)
    /// @return true if request is now allowed, false if maxWait exceeded
    virtual bool waitIfNeeded(
        std::chrono::milliseconds maxWait = std::chrono::milliseconds{0}
    ) = 0;

    /// Record that a request was made (decrements counters)
    virtual void recordRequest() = 0;

    //=========================================================================
    // State Updates
    //=========================================================================

    /// Update rate limit state from HTTP response headers
    /// Parses headers like: x-rl-daily-remaining, x-rl-hourly-remaining, etc.
    /// @param headers Response headers to parse
    virtual void updateFromHeaders(const Headers& headers) = 0;

    /// Manually set rate limit values (for testing or manual override)
    virtual void setLimits(
        int dailyRemaining, int dailyLimit, std::chrono::system_clock::time_point dailyReset,
        int hourlyRemaining, int hourlyLimit, std::chrono::system_clock::time_point hourlyReset
    ) = 0;

    //=========================================================================
    // State Access
    //=========================================================================

    /// Get current rate limit status
    virtual RateLimitStatus status() const = 0;

    /// Get daily requests remaining
    virtual int dailyRemaining() const = 0;

    /// Get hourly requests remaining
    virtual int hourlyRemaining() const = 0;

    //=========================================================================
    // Persistence
    //=========================================================================

    /// Save rate limit state to file
    /// @param path File path to save state
    virtual void saveState(const std::filesystem::path& path) const = 0;

    /// Load rate limit state from file
    /// @param path File path to load state from
    /// @return true if state was loaded successfully
    virtual bool loadState(const std::filesystem::path& path) = 0;

    //=========================================================================
    // Events
    //=========================================================================

    /// Callback type for rate limit warnings
    using WarningCallback = std::function<void(const RateLimitStatus&)>;

    /// Set callback for when rate limits are low
    /// @param threshold Trigger when remaining drops below this
    /// @param callback Function to call with current status
    virtual void onLowLimit(int threshold, WarningCallback callback) = 0;
};

/// Shared pointer type for rate limiters
using RateLimiterPtr = std::shared_ptr<IRateLimiter>;

} // namespace modular::fluent
