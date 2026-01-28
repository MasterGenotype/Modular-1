#ifndef MODULAR_RATELIMITER_H
#define MODULAR_RATELIMITER_H

#include <chrono>
#include <string>
#include <map>
#include <filesystem>
#include "ILogger.h"

namespace modular {

/**
 * @brief Tracks and enforces NexusMods API rate limits
 * 
 * NexusMods enforces:
 * - 20,000 requests per 24-hour period (resets at 00:00 GMT)
 * - 500 requests per hour after daily limit reached (resets on the hour)
 * 
 * CRITICAL: Stores reset TIMESTAMPS not just remaining counts.
 * Without reset times, waitIfNeeded() doesn't know how long to sleep.
 */
class RateLimiter {
public:
    explicit RateLimiter(ILogger& logger);
    
    /**
     * @brief Update rate limit state from API response headers
     * 
     * Parses headers case-insensitively:
     * - x-rl-daily-remaining, x-rl-daily-limit, x-rl-daily-reset
     * - x-rl-hourly-remaining, x-rl-hourly-limit, x-rl-hourly-reset
     * 
     * Reset headers should contain Unix timestamps (seconds since epoch)
     */
    void updateFromHeaders(const std::map<std::string, std::string>& headers);
    
    /**
     * @brief Check if we can make a request without waiting
     */
    bool canMakeRequest() const;
    
    /**
     * @brief Block until rate limits allow a request
     * 
     * Logic:
     * - If hourly = 0 but daily > 0: sleep until hourly_reset
     * - If daily = 0: sleep until daily_reset
     * - Choose the soonest reset that unblocks you
     */
    void waitIfNeeded();
    
    // Getters for UI display
    int getDailyRemaining() const { return daily_remaining_; }
    int getHourlyRemaining() const { return hourly_remaining_; }
    int getDailyLimit() const { return daily_limit_; }
    int getHourlyLimit() const { return hourly_limit_; }
    
    std::chrono::system_clock::time_point getDailyReset() const { return daily_reset_; }
    std::chrono::system_clock::time_point getHourlyReset() const { return hourly_reset_; }
    
    // Persistence
    void saveState(const std::filesystem::path& path);
    void loadState(const std::filesystem::path& path);

private:
    ILogger& logger_;
    
    // Rate limit state
    int daily_limit_ = 20000;
    int daily_remaining_ = 20000;
    int hourly_limit_ = 500;
    int hourly_remaining_ = 500;
    
    // CRITICAL: Reset timestamps (not just counts)
    std::chrono::system_clock::time_point daily_reset_;
    std::chrono::system_clock::time_point hourly_reset_;
    
    // Helper to parse header value (case-insensitive key lookup)
    std::string getHeaderValue(const std::map<std::string, std::string>& headers,
                               const std::string& key) const;
    
    // Helper to parse Unix timestamp from string
    std::chrono::system_clock::time_point parseTimestamp(const std::string& ts_str) const;
};

} // namespace modular

#endif // MODULAR_RATELIMITER_H
