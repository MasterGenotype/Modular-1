#ifndef MODULAR_EXCEPTIONS_H
#define MODULAR_EXCEPTIONS_H

#include <stdexcept>
#include <string>
#include <optional>

namespace modular {

/**
 * @brief Base exception for all Modular errors
 * 
 * Includes context payloads (url, response snippet) to aid debugging
 */
class ModularException : public std::runtime_error {
public:
    explicit ModularException(const std::string& msg, const std::string& url = "")
        : std::runtime_error(msg), url_(url) {}
    
    const std::string& url() const { return url_; }
    
    void setContext(const std::string& ctx) { context_ = ctx; }
    const std::string& context() const { return context_; }
    
    void setResponseSnippet(const std::string& snippet) { 
        response_snippet_ = snippet.substr(0, 500);  // Limit to 500 chars
    }
    const std::string& responseSnippet() const { return response_snippet_; }

protected:
    std::string url_;
    std::string context_;
    std::string response_snippet_;
};

/**
 * @brief Network-level errors (connection failures, timeouts, DNS, etc.)
 */
class NetworkException : public ModularException {
public:
    NetworkException(const std::string& msg, const std::string& url = "", int curl_code = 0)
        : ModularException(msg, url), curl_code_(curl_code) {}
    
    int curlCode() const { return curl_code_; }

private:
    int curl_code_;
};

/**
 * @brief HTTP-level errors (4xx, 5xx status codes)
 */
class ApiException : public ModularException {
public:
    ApiException(long status, const std::string& msg, const std::string& url = "")
        : ModularException(msg, url), status_code_(status) {}
    
    long statusCode() const { return status_code_; }
    
    void setRequestId(const std::string& id) { request_id_ = id; }
    const std::optional<std::string>& requestId() const { return request_id_; }

private:
    long status_code_;
    std::optional<std::string> request_id_;
};

/**
 * @brief Rate limit exceeded (429 Too Many Requests)
 * 
 * Includes retry_after duration if provided by server
 */
class RateLimitException : public ApiException {
public:
    explicit RateLimitException(const std::string& msg, const std::string& url = "")
        : ApiException(429, msg, url) {}
    
    void setRetryAfter(int seconds) { retry_after_seconds_ = seconds; }
    std::optional<int> retryAfter() const { return retry_after_seconds_; }

private:
    std::optional<int> retry_after_seconds_;
};

/**
 * @brief Authentication/authorization failures (401, 403)
 */
class AuthException : public ApiException {
public:
    AuthException(long status, const std::string& msg, const std::string& url = "")
        : ApiException(status, msg, url) {}
};

/**
 * @brief JSON parsing errors
 */
class ParseException : public ModularException {
public:
    ParseException(const std::string& msg, const std::string& url = "")
        : ModularException(msg, url) {}
    
    void setJsonSnippet(const std::string& snippet) {
        json_snippet_ = snippet.substr(0, 200);
    }
    const std::string& jsonSnippet() const { return json_snippet_; }

private:
    std::string json_snippet_;
};

/**
 * @brief File system errors (read/write failures, permission denied, etc.)
 */
class FileSystemException : public ModularException {
public:
    FileSystemException(const std::string& msg, const std::string& path = "")
        : ModularException(msg, path) {}
};

/**
 * @brief Configuration errors (missing keys, invalid values, etc.)
 */
class ConfigException : public ModularException {
public:
    explicit ConfigException(const std::string& msg)
        : ModularException(msg) {}
};

} // namespace modular

#endif // MODULAR_EXCEPTIONS_H
