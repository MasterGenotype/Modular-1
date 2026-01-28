#include "RetryCoordinator.h"

#include <algorithm>

namespace modular::fluent {

RetryCoordinator::RetryCoordinator(
    std::shared_ptr<IRetryConfig> config,
    modular::ILogger* logger
)
    : logger_(logger)
{
    if (config) {
        configs_.push_back(std::move(config));
    }
}

RetryCoordinator::RetryCoordinator(
    std::vector<std::shared_ptr<IRetryConfig>> configs,
    modular::ILogger* logger
)
    : configs_(std::move(configs))
    , logger_(logger)
{}

std::future<ResponsePtr> RetryCoordinator::executeAsync(
    IRequest& request,
    std::function<std::future<ResponsePtr>(IRequest&)> dispatcher
) {
    return std::async(std::launch::async, [this, &request, dispatcher]() -> ResponsePtr {
        int maxRetries = getMaxRetries();
        int attempt = 0;

        while (true) {
            ++attempt;

            try {
                // Execute the request
                auto responseFuture = dispatcher(request);
                auto response = responseFuture.get();

                // Check if we should retry based on status code
                bool isTimeout = false;  // Response received, so not a timeout
                int statusCode = response->statusCode();

                if (shouldRetry(statusCode, isTimeout) && attempt <= maxRetries) {
                    auto delay = getDelay(attempt, statusCode);

                    if (logger_) {
                        logger_->warn(
                            "RetryCoordinator: Retrying request (attempt " +
                            std::to_string(attempt) + "/" + std::to_string(maxRetries) +
                            ") after status " + std::to_string(statusCode) +
                            ", waiting " + std::to_string(delay.count()) + "ms"
                        );
                    }

                    std::this_thread::sleep_for(delay);
                    continue;
                }

                // Success or no retry needed
                return response;

            } catch (const NetworkException& ex) {
                // Check if we should retry network errors
                bool isTimeout = ex.isTimeout();

                if (shouldRetry(0, isTimeout) && attempt <= maxRetries) {
                    auto delay = getDelay(attempt, 0);

                    if (logger_) {
                        logger_->warn(
                            "RetryCoordinator: Retrying request (attempt " +
                            std::to_string(attempt) + "/" + std::to_string(maxRetries) +
                            ") after network error: " + ex.what() +
                            ", waiting " + std::to_string(delay.count()) + "ms"
                        );
                    }

                    std::this_thread::sleep_for(delay);
                    continue;
                }

                // No more retries, rethrow
                throw;
            }
        }
    });
}

void RetryCoordinator::addConfig(std::shared_ptr<IRetryConfig> config) {
    if (config) {
        configs_.push_back(std::move(config));
    }
}

void RetryCoordinator::clearConfigs() {
    configs_.clear();
}

void RetryCoordinator::setLogger(modular::ILogger* logger) {
    logger_ = logger;
}

bool RetryCoordinator::shouldRetry(int statusCode, bool isTimeout) const {
    // Return true if ANY config says to retry
    for (const auto& config : configs_) {
        if (config->shouldRetry(statusCode, isTimeout)) {
            return true;
        }
    }
    return false;
}

std::chrono::milliseconds RetryCoordinator::getDelay(int attempt, int statusCode) const {
    // Return the maximum delay from all configs that say to retry
    std::chrono::milliseconds maxDelay{0};

    for (const auto& config : configs_) {
        if (config->shouldRetry(statusCode, false) || config->shouldRetry(0, true)) {
            auto delay = config->getDelay(attempt, statusCode);
            maxDelay = std::max(maxDelay, delay);
        }
    }

    return maxDelay;
}

int RetryCoordinator::getMaxRetries() const {
    // Return the maximum number of retries from all configs
    int maxRetries = 0;
    for (const auto& config : configs_) {
        maxRetries = std::max(maxRetries, config->maxRetries());
    }
    return maxRetries;
}

} // namespace modular::fluent
