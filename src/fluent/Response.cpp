#include "Response.h"
#include <fstream>
#include <algorithm>

namespace modular::fluent {

//=============================================================================
// Constructors
//=============================================================================

Response::Response(
    int statusCode,
    std::string statusReason,
    Headers headers,
    std::vector<uint8_t> body,
    std::string effectiveUrl,
    std::chrono::milliseconds elapsed
)
    : statusCode_(statusCode)
    , statusReason_(std::move(statusReason))
    , headers_(std::move(headers))
    , body_(std::move(body))
    , effectiveUrl_(std::move(effectiveUrl))
    , elapsed_(elapsed)
    , originalUrl_(effectiveUrl_)
{}

Response::Response(
    int statusCode,
    std::string statusReason,
    Headers headers,
    std::string body,
    std::string effectiveUrl,
    std::chrono::milliseconds elapsed
)
    : statusCode_(statusCode)
    , statusReason_(std::move(statusReason))
    , headers_(std::move(headers))
    , body_(body.begin(), body.end())
    , effectiveUrl_(std::move(effectiveUrl))
    , elapsed_(elapsed)
    , originalUrl_(effectiveUrl_)
{}

//=============================================================================
// Status Information
//=============================================================================

bool Response::isSuccessStatusCode() const {
    return is_success_status(statusCode_);
}

int Response::statusCode() const {
    return statusCode_;
}

std::string Response::statusReason() const {
    return statusReason_;
}

//=============================================================================
// Headers
//=============================================================================

const Headers& Response::headers() const {
    return headers_;
}

std::string Response::header(std::string_view name) const {
    return findHeaderValue(name);
}

bool Response::hasHeader(std::string_view name) const {
    return !findHeaderValue(name).empty();
}

std::string Response::contentType() const {
    return findHeaderValue("Content-Type");
}

int64_t Response::contentLength() const {
    auto value = findHeaderValue("Content-Length");
    if (value.empty()) return -1;
    try {
        return std::stoll(value);
    } catch (...) {
        return -1;
    }
}

std::string Response::findHeaderValue(std::string_view name) const {
    return detail::findHeader(headers_, name);
}

//=============================================================================
// Body Access - Synchronous
//=============================================================================

std::string Response::asString() {
    std::lock_guard<std::mutex> lock(cacheMutex_);

    if (!cachedString_) {
        cachedString_ = std::string(body_.begin(), body_.end());
    }
    return *cachedString_;
}

std::vector<uint8_t> Response::asByteArray() {
    return body_;
}

nlohmann::json Response::asJson() {
    std::lock_guard<std::mutex> lock(cacheMutex_);

    if (!cachedJson_) {
        try {
            std::string str(body_.begin(), body_.end());
            cachedJson_ = nlohmann::json::parse(str);
        } catch (const nlohmann::json::parse_error& e) {
            throw ParseException(
                std::string("Failed to parse JSON: ") + e.what(),
                std::string(body_.begin(), body_.end())
            );
        }
    }
    return *cachedJson_;
}

//=============================================================================
// Body Access - Asynchronous
//=============================================================================

std::future<std::string> Response::asStringAsync() {
    return std::async(std::launch::async, [this]() {
        return asString();
    });
}

std::future<std::vector<uint8_t>> Response::asByteArrayAsync() {
    return std::async(std::launch::async, [this]() {
        return asByteArray();
    });
}

std::future<nlohmann::json> Response::asJsonAsync() {
    return std::async(std::launch::async, [this]() {
        return asJson();
    });
}

//=============================================================================
// File Operations
//=============================================================================

void Response::saveToFile(
    const std::filesystem::path& path,
    ProgressCallback progress
) {
    // Ensure parent directory exists
    if (path.has_parent_path()) {
        std::filesystem::create_directories(path.parent_path());
    }

    std::ofstream file(path, std::ios::binary);
    if (!file) {
        throw std::filesystem::filesystem_error(
            "Failed to open file for writing",
            path,
            std::make_error_code(std::errc::io_error)
        );
    }

    const size_t total = body_.size();
    const size_t chunkSize = 8192;

    for (size_t written = 0; written < total; written += chunkSize) {
        size_t toWrite = std::min(chunkSize, total - written);
        file.write(reinterpret_cast<const char*>(body_.data() + written), toWrite);

        if (!file) {
            throw std::filesystem::filesystem_error(
                "Failed to write to file",
                path,
                std::make_error_code(std::errc::io_error)
            );
        }

        if (progress) {
            progress(written + toWrite, total);
        }
    }
}

std::future<void> Response::saveToFileAsync(
    const std::filesystem::path& path,
    ProgressCallback progress
) {
    return std::async(std::launch::async, [this, path, progress]() {
        saveToFile(path, progress);
    });
}

//=============================================================================
// Metadata
//=============================================================================

std::string Response::effectiveUrl() const {
    return effectiveUrl_;
}

std::chrono::milliseconds Response::elapsed() const {
    return elapsed_;
}

bool Response::wasRedirected() const {
    return effectiveUrl_ != originalUrl_;
}

//=============================================================================
// Factory Methods
//=============================================================================

std::unique_ptr<Response> Response::fromHttpClientResponse(
    int statusCode,
    const std::string& body,
    const std::map<std::string, std::string>& headers,
    const std::string& url,
    std::chrono::milliseconds elapsed
) {
    return std::make_unique<Response>(
        statusCode,
        detail::getStatusReason(statusCode),
        headers,
        body,
        url,
        elapsed
    );
}

} // namespace modular::fluent
