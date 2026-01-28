#pragma once

#include <fluent/IHttpFilter.h>
#include <fluent/IRequest.h>
#include <fluent/IResponse.h>
#include <fluent/IRateLimiter.h>
#include <fluent/Exceptions.h>
#include <core/ILogger.h>

namespace modular::fluent {

/// Filter that tracks and enforces NexusMods rate limits.
/// Reads rate limit headers from responses and updates the rate limiter.
/// Can optionally block requests when limits are exhausted.
///
/// NexusMods Headers:
/// - X-RL-Daily-Limit / X-RL-Daily-Remaining / X-RL-Daily-Reset
/// - X-RL-Hourly-Limit / X-RL-Hourly-Remaining / X-RL-Hourly-Reset
///
/// Priority 500 (middle - after auth, before error handling)
class RateLimitFilter : public IHttpFilter {
public:
    explicit RateLimitFilter(
        std::shared_ptr<IRateLimiter> rateLimiter,
        modular::ILogger* logger = nullptr,
        bool blockOnLimit = true
    )
        : rateLimiter_(std::move(rateLimiter))
        , logger_(logger)
        , blockOnLimit_(blockOnLimit)
    {}

    ~RateLimitFilter() override = default;

    void onRequest(IRequest& /*request*/) override {
        if (!rateLimiter_ || !blockOnLimit_) return;

        // Check if we can make a request
        if (!rateLimiter_->canMakeRequest()) {
            auto st = rateLimiter_->status();

            if (logger_) {
                logger_->warn(
                    "Rate limit exhausted. Remaining: daily=" +
                    std::to_string(st.dailyRemaining) +
                    ", hourly=" + std::to_string(st.hourlyRemaining)
                );
            }

            // Calculate time until reset
            auto now = std::chrono::system_clock::now();
            auto resetTime = st.hourlyRemaining <= 0 ? st.hourlyReset : st.dailyReset;
            auto waitTime = resetTime > now
                ? std::chrono::duration_cast<std::chrono::seconds>(resetTime - now)
                : std::chrono::seconds{60};

            throw RateLimitException(
                "Rate limit exhausted",
                Headers{},
                "",
                waitTime
            );
        }
    }

    void onResponse(IResponse& response, bool /*httpErrorAsException*/) override {
        if (!rateLimiter_) return;

        // Update rate limiter from response headers
        rateLimiter_->updateFromHeaders(response.headers());

        if (logger_) {
            auto st = rateLimiter_->status();
            logger_->debug(
                "Rate limits: daily=" + std::to_string(st.dailyRemaining) +
                "/" + std::to_string(st.dailyLimit) +
                ", hourly=" + std::to_string(st.hourlyRemaining) +
                "/" + std::to_string(st.hourlyLimit)
            );
        }
    }

    std::string name() const override { return "RateLimitFilter"; }

    int priority() const override { return 500; }

    void setBlockOnLimit(bool block) { blockOnLimit_ = block; }
    bool blockOnLimit() const { return blockOnLimit_; }

    std::shared_ptr<IRateLimiter> rateLimiter() const { return rateLimiter_; }

private:
    std::shared_ptr<IRateLimiter> rateLimiter_;
    modular::ILogger* logger_;
    bool blockOnLimit_;
};

} // namespace modular::fluent
