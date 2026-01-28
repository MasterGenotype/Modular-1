#pragma once

#include <fluent/IRequestCoordinator.h>
#include <fluent/IRetryConfig.h>
#include <fluent/Exceptions.h>
#include <core/ILogger.h>

#include <vector>
#include <chrono>
#include <thread>

namespace modular::fluent {

/// Coordinator that implements retry logic with configurable policies.
/// Multiple retry configs can be chained; request is retried if ANY config says to retry.
class RetryCoordinator : public IRequestCoordinator {
public:
    /// Construct with a single retry config
    explicit RetryCoordinator(
        std::shared_ptr<IRetryConfig> config,
        modular::ILogger* logger = nullptr
    );

    /// Construct with multiple retry configs (will try each in order)
    RetryCoordinator(
        std::vector<std::shared_ptr<IRetryConfig>> configs,
        modular::ILogger* logger = nullptr
    );

    ~RetryCoordinator() override = default;

    //=========================================================================
    // IRequestCoordinator Implementation
    //=========================================================================

    std::future<ResponsePtr> executeAsync(
        IRequest& request,
        std::function<std::future<ResponsePtr>(IRequest&)> dispatcher
    ) override;

    std::string name() const override { return "RetryCoordinator"; }

    //=========================================================================
    // Configuration
    //=========================================================================

    /// Add a retry config
    void addConfig(std::shared_ptr<IRetryConfig> config);

    /// Remove all configs
    void clearConfigs();

    /// Set the logger
    void setLogger(modular::ILogger* logger);

private:
    std::vector<std::shared_ptr<IRetryConfig>> configs_;
    modular::ILogger* logger_;

    /// Check if any config says to retry
    bool shouldRetry(int statusCode, bool isTimeout) const;

    /// Get the maximum delay from all configs
    std::chrono::milliseconds getDelay(int attempt, int statusCode) const;

    /// Get total max retries from all configs
    int getMaxRetries() const;
};

} // namespace modular::fluent
