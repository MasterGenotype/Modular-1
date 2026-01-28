#include "RateLimiter.h"
#include "Exceptions.h"
#include <algorithm>
#include <cctype>
#include <thread>
#include <fstream>
#include <nlohmann/json.hpp>

using json = nlohmann::json;

namespace modular {

RateLimiter::RateLimiter(ILogger& logger) 
    : logger_(logger) {
    // Initialize reset times to now + 1 hour/day
    auto now = std::chrono::system_clock::now();
    hourly_reset_ = now + std::chrono::hours(1);
    daily_reset_ = now + std::chrono::hours(24);
}

std::string RateLimiter::getHeaderValue(
    const std::map<std::string, std::string>& headers,
    const std::string& key) const {
    
    // Case-insensitive lookup
    std::string key_lower = key;
    std::transform(key_lower.begin(), key_lower.end(), key_lower.begin(),
                   [](unsigned char c) { return std::tolower(c); });
    
    for (const auto& [header_key, header_value] : headers) {
        std::string header_key_lower = header_key;
        std::transform(header_key_lower.begin(), header_key_lower.end(), 
                      header_key_lower.begin(),
                      [](unsigned char c) { return std::tolower(c); });
        
        if (header_key_lower == key_lower) {
            return header_value;
        }
    }
    
    return "";
}

std::chrono::system_clock::time_point RateLimiter::parseTimestamp(
    const std::string& ts_str) const {
    
    if (ts_str.empty()) {
        return std::chrono::system_clock::now();
    }
    
    try {
        // Parse Unix timestamp (seconds since epoch)
        long long timestamp = std::stoll(ts_str);
        auto duration = std::chrono::seconds(timestamp);
        return std::chrono::system_clock::time_point(duration);
    } catch (...) {
        logger_.warn("Failed to parse timestamp: " + ts_str);
        return std::chrono::system_clock::now();
    }
}

void RateLimiter::updateFromHeaders(
    const std::map<std::string, std::string>& headers) {
    
    // Parse daily limits
    std::string daily_limit = getHeaderValue(headers, "x-rl-daily-limit");
    if (!daily_limit.empty()) {
        daily_limit_ = std::stoi(daily_limit);
    }
    
    std::string daily_remaining = getHeaderValue(headers, "x-rl-daily-remaining");
    if (!daily_remaining.empty()) {
        daily_remaining_ = std::stoi(daily_remaining);
    }
    
    std::string daily_reset = getHeaderValue(headers, "x-rl-daily-reset");
    if (!daily_reset.empty()) {
        daily_reset_ = parseTimestamp(daily_reset);
    }
    
    // Parse hourly limits
    std::string hourly_limit = getHeaderValue(headers, "x-rl-hourly-limit");
    if (!hourly_limit.empty()) {
        hourly_limit_ = std::stoi(hourly_limit);
    }
    
    std::string hourly_remaining = getHeaderValue(headers, "x-rl-hourly-remaining");
    if (!hourly_remaining.empty()) {
        hourly_remaining_ = std::stoi(hourly_remaining);
    }
    
    std::string hourly_reset = getHeaderValue(headers, "x-rl-hourly-reset");
    if (!hourly_reset.empty()) {
        hourly_reset_ = parseTimestamp(hourly_reset);
    }
    
    // Log current state
    logger_.debug("Rate limits updated: Daily=" + std::to_string(daily_remaining_) +
                  "/" + std::to_string(daily_limit_) +
                  ", Hourly=" + std::to_string(hourly_remaining_) +
                  "/" + std::to_string(hourly_limit_));
}

bool RateLimiter::canMakeRequest() const {
    // We can make a request if we have either daily or hourly quota
    return daily_remaining_ > 0 && hourly_remaining_ > 0;
}

void RateLimiter::waitIfNeeded() {
    if (canMakeRequest()) {
        return;  // No need to wait
    }
    
    auto now = std::chrono::system_clock::now();
    
    // Determine which limit is blocking us and when it resets
    std::chrono::system_clock::time_point wait_until;
    std::string reason;
    
    if (daily_remaining_ <= 0) {
        // Daily limit exhausted - wait until daily reset
        wait_until = daily_reset_;
        reason = "Daily rate limit exhausted";
    } else if (hourly_remaining_ <= 0) {
        // Hourly limit exhausted but daily still available
        wait_until = hourly_reset_;
        reason = "Hourly rate limit exhausted";
    } else {
        // Shouldn't reach here, but handle gracefully
        logger_.warn("waitIfNeeded() called but canMakeRequest() is true");
        return;
    }
    
    // Calculate sleep duration
    auto sleep_duration = wait_until - now;
    
    if (sleep_duration.count() <= 0) {
        // Reset time has already passed, we're good to go
        logger_.info("Rate limit reset time has passed, proceeding");
        return;
    }
    
    // Convert to seconds for logging
    auto sleep_seconds = std::chrono::duration_cast<std::chrono::seconds>(sleep_duration).count();
    
    logger_.warn(reason + ". Waiting " + std::to_string(sleep_seconds) + 
                 " seconds until reset...");
    
    // Sleep until reset
    std::this_thread::sleep_until(wait_until);
    
    logger_.info("Rate limit reset reached, resuming operations");
}

void RateLimiter::saveState(const std::filesystem::path& path) {
    try {
        json state;
        
        state["daily_limit"] = daily_limit_;
        state["daily_remaining"] = daily_remaining_;
        state["hourly_limit"] = hourly_limit_;
        state["hourly_remaining"] = hourly_remaining_;
        
        // Save timestamps as Unix epoch seconds
        auto daily_reset_epoch = std::chrono::duration_cast<std::chrono::seconds>(
            daily_reset_.time_since_epoch()).count();
        auto hourly_reset_epoch = std::chrono::duration_cast<std::chrono::seconds>(
            hourly_reset_.time_since_epoch()).count();
        
        state["daily_reset"] = daily_reset_epoch;
        state["hourly_reset"] = hourly_reset_epoch;
        
        std::ofstream file(path);
        if (!file) {
            throw FileSystemException("Failed to open file for writing", path.string());
        }
        
        file << state.dump(2);
        logger_.debug("Saved rate limiter state to " + path.string());
        
    } catch (const std::exception& e) {
        logger_.error("Failed to save rate limiter state: " + std::string(e.what()));
    }
}

void RateLimiter::loadState(const std::filesystem::path& path) {
    try {
        if (!std::filesystem::exists(path)) {
            logger_.debug("No saved rate limiter state found");
            return;
        }
        
        std::ifstream file(path);
        if (!file) {
            throw FileSystemException("Failed to open file for reading", path.string());
        }
        
        json state = json::parse(file);
        
        daily_limit_ = state.value("daily_limit", 20000);
        daily_remaining_ = state.value("daily_remaining", 20000);
        hourly_limit_ = state.value("hourly_limit", 500);
        hourly_remaining_ = state.value("hourly_remaining", 500);
        
        // Load timestamps
        if (state.contains("daily_reset")) {
            long long epoch = state["daily_reset"];
            daily_reset_ = std::chrono::system_clock::time_point(std::chrono::seconds(epoch));
        }
        
        if (state.contains("hourly_reset")) {
            long long epoch = state["hourly_reset"];
            hourly_reset_ = std::chrono::system_clock::time_point(std::chrono::seconds(epoch));
        }
        
        logger_.debug("Loaded rate limiter state from " + path.string());
        
    } catch (const std::exception& e) {
        logger_.error("Failed to load rate limiter state: " + std::string(e.what()));
    }
}

} // namespace modular
