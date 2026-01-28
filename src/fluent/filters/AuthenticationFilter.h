#pragma once

#include <fluent/IHttpFilter.h>
#include <fluent/IRequest.h>
#include <fluent/IResponse.h>

#include <functional>

namespace modular::fluent {

/// Filter that adds authentication to requests.
/// Priority 200 (early, after diagnostic filters).
///
/// Supports multiple authentication styles:
/// - Static header (e.g., API key)
/// - Bearer token
/// - Dynamic token (refreshable)
class AuthenticationFilter : public IHttpFilter {
public:
    /// Create with a static header value
    AuthenticationFilter(std::string headerName, std::string headerValue)
        : headerName_(std::move(headerName))
        , staticValue_(std::move(headerValue))
        , mode_(Mode::Static)
    {}

    /// Create with Bearer token
    static std::shared_ptr<AuthenticationFilter> bearer(const std::string& token) {
        return std::make_shared<AuthenticationFilter>("Authorization", "Bearer " + token);
    }

    /// Create with API key header (common for NexusMods)
    static std::shared_ptr<AuthenticationFilter> apiKey(const std::string& key) {
        return std::make_shared<AuthenticationFilter>("apikey", key);
    }

    /// Create with a token provider (for refreshable tokens)
    static std::shared_ptr<AuthenticationFilter> dynamic(
        std::function<std::string()> tokenProvider
    ) {
        auto filter = std::make_shared<AuthenticationFilter>("Authorization", "");
        filter->tokenProvider_ = std::move(tokenProvider);
        filter->mode_ = Mode::Dynamic;
        return filter;
    }

    ~AuthenticationFilter() override = default;

    void onRequest(IRequest& request) override {
        std::string value;

        switch (mode_) {
            case Mode::Static:
                value = staticValue_;
                break;
            case Mode::Dynamic:
                if (tokenProvider_) {
                    value = "Bearer " + tokenProvider_();
                }
                break;
        }

        if (!value.empty()) {
            request.withHeader(headerName_, value);
        }
    }

    void onResponse(IResponse& /*response*/, bool /*httpErrorAsException*/) override {
        // Nothing to do on response
    }

    std::string name() const override { return "AuthenticationFilter"; }

    /// Runs early to ensure auth is set before other filters
    int priority() const override { return 200; }

    /// Update the static value
    void setValue(const std::string& value) {
        staticValue_ = value;
    }

    /// Update the header name
    void setHeaderName(const std::string& name) {
        headerName_ = name;
    }

private:
    enum class Mode { Static, Dynamic };

    std::string headerName_;
    std::string staticValue_;
    std::function<std::string()> tokenProvider_;
    Mode mode_ = Mode::Static;
};

} // namespace modular::fluent
