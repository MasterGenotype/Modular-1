#include "HttpClientBridge.h"
#include "Utils.h"

#include <curl/curl.h>
#include <stdexcept>
#include <chrono>

namespace modular::fluent {

//=============================================================================
// CURL Callbacks
//=============================================================================

namespace {

struct WriteContext {
    std::vector<uint8_t>* buffer;
    std::function<void(const uint8_t*, size_t)> streamCallback;
};

size_t writeCallback(char* ptr, size_t size, size_t nmemb, void* userdata) {
    auto* ctx = static_cast<WriteContext*>(userdata);
    size_t totalSize = size * nmemb;

    if (ctx->streamCallback) {
        ctx->streamCallback(reinterpret_cast<uint8_t*>(ptr), totalSize);
    } else if (ctx->buffer) {
        ctx->buffer->insert(ctx->buffer->end(), ptr, ptr + totalSize);
    }

    return totalSize;
}

struct HeaderContext {
    Headers* headers;
};

size_t headerCallback(char* buffer, size_t size, size_t nitems, void* userdata) {
    auto* ctx = static_cast<HeaderContext*>(userdata);
    size_t totalSize = size * nitems;

    std::string header(buffer, totalSize);

    // Parse header line
    auto colonPos = header.find(':');
    if (colonPos != std::string::npos) {
        std::string key = header.substr(0, colonPos);
        std::string value = header.substr(colonPos + 1);

        // Trim whitespace
        value = detail::trim(value);
        (*ctx->headers)[key] = value;
    }

    return totalSize;
}

struct ProgressContext {
    ProgressCallback callback;
    std::chrono::steady_clock::time_point lastUpdate;
    static constexpr auto minInterval = std::chrono::milliseconds{100};
};

int progressCallback(
    void* userdata,
    curl_off_t dltotal, curl_off_t dlnow,
    curl_off_t /*ultotal*/, curl_off_t /*ulnow*/
) {
    auto* ctx = static_cast<ProgressContext*>(userdata);

    if (!ctx->callback) return 0;

    auto now = std::chrono::steady_clock::now();
    if (now - ctx->lastUpdate >= ctx->minInterval || dlnow == dltotal) {
        ctx->lastUpdate = now;
        ctx->callback(static_cast<size_t>(dlnow), static_cast<size_t>(dltotal));
    }

    return 0;  // Return non-zero to abort
}

} // anonymous namespace

//=============================================================================
// Implementation Class
//=============================================================================

class HttpClientBridge::Impl {
public:
    explicit Impl(modular::ILogger* logger)
        : logger_(logger)
        , curl_(curl_easy_init())
    {
        if (!curl_) {
            throw std::runtime_error("Failed to initialize CURL");
        }

        // Set default options
        curl_easy_setopt(curl_, CURLOPT_SSL_VERIFYPEER, 1L);
        curl_easy_setopt(curl_, CURLOPT_SSL_VERIFYHOST, 2L);
        curl_easy_setopt(curl_, CURLOPT_FOLLOWLOCATION, 1L);
        curl_easy_setopt(curl_, CURLOPT_MAXREDIRS, 5L);
        curl_easy_setopt(curl_, CURLOPT_CONNECTTIMEOUT, 30L);
        curl_easy_setopt(curl_, CURLOPT_TIMEOUT, 60L);
    }

    ~Impl() {
        if (curl_) {
            curl_easy_cleanup(curl_);
        }
    }

    HttpResult execute(const HttpRequestConfig& config) {
        return executeInternal(config, nullptr, nullptr);
    }

    HttpResult executeStreaming(
        const HttpRequestConfig& config,
        std::function<void(const uint8_t*, size_t)> onData,
        ProgressCallback onProgress
    ) {
        return executeInternal(config, std::move(onData), std::move(onProgress));
    }

    void setConnectionTimeout(std::chrono::seconds timeout) {
        connectionTimeout_ = timeout;
    }

    void setSslVerification(bool verify) {
        sslVerify_ = verify;
    }

    void setProxy(const std::string& proxyUrl) {
        proxy_ = proxyUrl;
    }

    void setLogger(modular::ILogger* logger) {
        logger_ = logger;
    }

private:
    HttpResult executeInternal(
        const HttpRequestConfig& config,
        std::function<void(const uint8_t*, size_t)> onData,
        ProgressCallback onProgress
    ) {
        auto startTime = std::chrono::steady_clock::now();

        // Reset CURL handle for new request
        curl_easy_reset(curl_);

        // Set URL
        curl_easy_setopt(curl_, CURLOPT_URL, config.url.c_str());

        // Set HTTP method
        switch (config.method) {
            case HttpMethod::GET:
                curl_easy_setopt(curl_, CURLOPT_HTTPGET, 1L);
                break;
            case HttpMethod::POST:
                curl_easy_setopt(curl_, CURLOPT_POST, 1L);
                break;
            case HttpMethod::PUT:
                curl_easy_setopt(curl_, CURLOPT_CUSTOMREQUEST, "PUT");
                break;
            case HttpMethod::PATCH:
                curl_easy_setopt(curl_, CURLOPT_CUSTOMREQUEST, "PATCH");
                break;
            case HttpMethod::DELETE:
                curl_easy_setopt(curl_, CURLOPT_CUSTOMREQUEST, "DELETE");
                break;
            case HttpMethod::HEAD:
                curl_easy_setopt(curl_, CURLOPT_NOBODY, 1L);
                break;
            case HttpMethod::OPTIONS:
                curl_easy_setopt(curl_, CURLOPT_CUSTOMREQUEST, "OPTIONS");
                break;
        }

        // Set headers
        struct curl_slist* headerList = nullptr;
        for (const auto& [key, value] : config.headers) {
            std::string header = key + ": " + value;
            headerList = curl_slist_append(headerList, header.c_str());
        }
        if (headerList) {
            curl_easy_setopt(curl_, CURLOPT_HTTPHEADER, headerList);
        }

        // Set request body
        if (config.body && !config.body->empty()) {
            curl_easy_setopt(curl_, CURLOPT_POSTFIELDS, config.body->data());
            curl_easy_setopt(curl_, CURLOPT_POSTFIELDSIZE, config.body->size());
        }

        // Set timeout
        curl_easy_setopt(curl_, CURLOPT_TIMEOUT, static_cast<long>(config.timeout.count()));
        curl_easy_setopt(curl_, CURLOPT_CONNECTTIMEOUT, static_cast<long>(connectionTimeout_.count()));

        // Set redirects
        curl_easy_setopt(curl_, CURLOPT_FOLLOWLOCATION, config.followRedirects ? 1L : 0L);
        curl_easy_setopt(curl_, CURLOPT_MAXREDIRS, static_cast<long>(config.maxRedirects));

        // Set SSL verification
        curl_easy_setopt(curl_, CURLOPT_SSL_VERIFYPEER, sslVerify_ ? 1L : 0L);
        curl_easy_setopt(curl_, CURLOPT_SSL_VERIFYHOST, sslVerify_ ? 2L : 0L);

        // Set proxy if configured
        if (!proxy_.empty()) {
            curl_easy_setopt(curl_, CURLOPT_PROXY, proxy_.c_str());
        }

        // Set up response handling
        std::vector<uint8_t> responseBody;
        WriteContext writeCtx{&responseBody, onData};
        curl_easy_setopt(curl_, CURLOPT_WRITEFUNCTION, writeCallback);
        curl_easy_setopt(curl_, CURLOPT_WRITEDATA, &writeCtx);

        Headers responseHeaders;
        HeaderContext headerCtx{&responseHeaders};
        curl_easy_setopt(curl_, CURLOPT_HEADERFUNCTION, headerCallback);
        curl_easy_setopt(curl_, CURLOPT_HEADERDATA, &headerCtx);

        // Set up progress
        ProgressContext progressCtx{onProgress, std::chrono::steady_clock::now()};
        if (onProgress) {
            curl_easy_setopt(curl_, CURLOPT_XFERINFOFUNCTION, progressCallback);
            curl_easy_setopt(curl_, CURLOPT_XFERINFODATA, &progressCtx);
            curl_easy_setopt(curl_, CURLOPT_NOPROGRESS, 0L);
        }

        // Log request
        if (logger_) {
            logger_->debug("HTTP " + std::string(to_string(config.method)) + " " + config.url);
        }

        // Execute request
        CURLcode res = curl_easy_perform(curl_);

        // Clean up headers
        if (headerList) {
            curl_slist_free_all(headerList);
        }

        auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now() - startTime
        );

        // Build result
        HttpResult result;
        result.elapsed = elapsed;
        result.headers = std::move(responseHeaders);
        result.body = onData ? std::vector<uint8_t>{} : std::move(responseBody);

        if (res != CURLE_OK) {
            result.wasTimeout = (res == CURLE_OPERATION_TIMEDOUT);

            if (logger_) {
                logger_->error("CURL error: " + std::string(curl_easy_strerror(res)));
            }

            throw NetworkException(
                std::string("Network error: ") + curl_easy_strerror(res),
                result.wasTimeout ? NetworkException::Reason::Timeout
                                  : NetworkException::Reason::ConnectionFailed
            );
        }

        // Get response info
        long statusCode;
        curl_easy_getinfo(curl_, CURLINFO_RESPONSE_CODE, &statusCode);
        result.statusCode = static_cast<int>(statusCode);

        char* effectiveUrl;
        curl_easy_getinfo(curl_, CURLINFO_EFFECTIVE_URL, &effectiveUrl);
        result.effectiveUrl = effectiveUrl ? effectiveUrl : config.url;

        // Determine status reason
        result.statusReason = detail::getStatusReason(result.statusCode);

        // Log response
        if (logger_) {
            logger_->debug("HTTP " + std::to_string(result.statusCode) + " in " +
                          std::to_string(elapsed.count()) + "ms");
        }

        return result;
    }

    modular::ILogger* logger_;
    CURL* curl_;
    std::chrono::seconds connectionTimeout_{30};
    bool sslVerify_ = true;
    std::string proxy_;
};

//=============================================================================
// HttpClientBridge Implementation
//=============================================================================

HttpClientBridge::HttpClientBridge(modular::ILogger* logger)
    : impl_(std::make_unique<Impl>(logger))
{}

HttpClientBridge::~HttpClientBridge() = default;

HttpClientBridge::HttpClientBridge(HttpClientBridge&&) noexcept = default;
HttpClientBridge& HttpClientBridge::operator=(HttpClientBridge&&) noexcept = default;

HttpResult HttpClientBridge::execute(const HttpRequestConfig& config) {
    return impl_->execute(config);
}

HttpResult HttpClientBridge::executeStreaming(
    const HttpRequestConfig& config,
    std::function<void(const uint8_t*, size_t)> onData,
    ProgressCallback onProgress
) {
    return impl_->executeStreaming(config, std::move(onData), std::move(onProgress));
}

void HttpClientBridge::setConnectionTimeout(std::chrono::seconds timeout) {
    impl_->setConnectionTimeout(timeout);
}

void HttpClientBridge::setSslVerification(bool verify) {
    impl_->setSslVerification(verify);
}

void HttpClientBridge::setProxy(const std::string& proxyUrl) {
    impl_->setProxy(proxyUrl);
}

void HttpClientBridge::setLogger(modular::ILogger* logger) {
    impl_->setLogger(logger);
}

} // namespace modular::fluent
