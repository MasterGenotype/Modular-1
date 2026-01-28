#pragma once

#include <fluent/IHttpFilter.h>
#include <fluent/IRequest.h>
#include <fluent/IResponse.h>
#include <core/ILogger.h>

namespace modular::fluent {

/// Filter that logs HTTP requests and responses.
/// Priority 100 (runs early on request, late on response).
///
/// Usage:
/// @code
/// auto logger = std::make_shared<StderrLogger>();
/// client->filters().add(std::make_shared<LoggingFilter>(logger.get()));
/// @endcode
class LoggingFilter : public IHttpFilter {
public:
    enum class LogLevel {
        Minimal,   // Just method + URL + status
        Normal,    // + timing
        Verbose    // + headers
    };

    explicit LoggingFilter(
        modular::ILogger* logger,
        LogLevel level = LogLevel::Normal
    )
        : logger_(logger)
        , level_(level)
    {}

    ~LoggingFilter() override = default;

    void onRequest(IRequest& request) override {
        if (!logger_) return;

        std::string msg = "--> " + std::string(to_string(request.method())) +
                         " " + request.url();

        if (level_ == LogLevel::Verbose) {
            const auto& headers = request.headers();
            for (const auto& [key, value] : headers) {
                // Redact sensitive headers
                if (key == "Authorization" || key == "apikey") {
                    msg += "\n    " + key + ": [REDACTED]";
                } else {
                    msg += "\n    " + key + ": " + value;
                }
            }
        }

        logger_->info(msg);
    }

    void onResponse(IResponse& response, bool /*httpErrorAsException*/) override {
        if (!logger_) return;

        std::string msg = "<-- " + std::to_string(response.statusCode()) +
                         " " + response.statusReason();

        if (level_ >= LogLevel::Normal) {
            msg += " (" + std::to_string(response.elapsed().count()) + "ms)";
        }

        if (level_ == LogLevel::Verbose) {
            const auto& headers = response.headers();
            for (const auto& [key, value] : headers) {
                msg += "\n    " + key + ": " + value;
            }
        }

        if (response.isSuccessStatusCode()) {
            logger_->info(msg);
        } else {
            logger_->warn(msg);
        }
    }

    std::string name() const override { return "LoggingFilter"; }

    /// Low priority so this runs early (sees original request)
    int priority() const override { return 100; }

    void setLevel(LogLevel level) { level_ = level; }
    LogLevel level() const { return level_; }

private:
    modular::ILogger* logger_;
    LogLevel level_;
};

} // namespace modular::fluent
