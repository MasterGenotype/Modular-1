#pragma once

#include <fluent/IHttpFilter.h>
#include <fluent/IResponse.h>
#include <fluent/Exceptions.h>

namespace modular::fluent {

/// Filter that throws ApiException on HTTP error status codes (4xx, 5xx).
/// This filter runs at high priority (9000) so it executes last on response.
///
/// Usage:
/// @code
/// client->filters().add(std::make_shared<DefaultErrorFilter>());
/// @endcode
class DefaultErrorFilter : public IHttpFilter {
public:
    DefaultErrorFilter() = default;
    ~DefaultErrorFilter() override = default;

    void onRequest(IRequest& /*request*/) override {
        // Nothing to do on request
    }

    void onResponse(IResponse& response, bool httpErrorAsException) override {
        if (!httpErrorAsException) {
            return;  // User wants to handle errors themselves
        }

        int status = response.statusCode();

        // Check for specific error types
        if (status == 429) {
            // Rate limit exceeded
            auto retryAfterStr = response.header("Retry-After");
            std::chrono::seconds retryAfter{60};  // Default to 60s

            if (!retryAfterStr.empty()) {
                try {
                    retryAfter = std::chrono::seconds{std::stoi(retryAfterStr)};
                } catch (...) {
                    // Keep default
                }
            }

            throw RateLimitException(
                "Rate limit exceeded",
                response.headers(),
                response.asString(),
                retryAfter
            );
        }

        if (status == 401 || status == 403) {
            throw AuthException(
                status == 401 ? "Unauthorized" : "Forbidden",
                status,
                response.headers(),
                response.asString()
            );
        }

        if (!response.isSuccessStatusCode()) {
            throw ApiException(
                "HTTP " + std::to_string(status) + ": " + response.statusReason(),
                status,
                response.statusReason(),
                response.headers(),
                response.asString()
            );
        }
    }

    std::string name() const override { return "DefaultErrorFilter"; }

    /// High priority so this runs last on response (after logging, etc.)
    int priority() const override { return 9000; }
};

} // namespace modular::fluent
