#ifndef MODULAR_HTTPCLIENT_H
#define MODULAR_HTTPCLIENT_H

#include <string>
#include <vector>
#include <map>
#include <functional>
#include <filesystem>
#include <curl/curl.h>
#include "RateLimiter.h"
#include "ILogger.h"

namespace modular {

/**
 * @brief HTTP response with status code, body, and headers
 */
struct HttpResponse {
    long status_code = 0;
    std::string body;
    std::map<std::string, std::string> headers;
};

/**
 * @brief Progress callback signature
 * @param downloaded Bytes downloaded so far
 * @param total Total bytes to download (0 if unknown)
 */
using ProgressCallback = std::function<void(size_t downloaded, size_t total)>;

/**
 * @brief HTTP request headers
 */
using Headers = std::vector<std::string>;

/**
 * @brief Retry policy for HTTP requests
 */
struct RetryPolicy {
    int max_retries = 3;
    int initial_delay_ms = 1000;  // 1 second
    int max_delay_ms = 16000;     // 16 seconds
    bool exponential_backoff = true;
};

/**
 * @brief Instance-based HTTP client with CURL handle ownership
 * 
 * Key design points from ChatGPT feedback:
 * - Instance-based (not all static) for testability and thread-safety
 * - Owns CURL easy handle (reusable connection)
 * - Owns reference to RateLimiter for automatic rate limiting
 * - Conditional retry logic (retry 5xx, don't retry 4xx except 429)
 * - Progress callbacks with CURLOPT_NOPROGRESS = 0 fix
 * - Parses response headers for RateLimiter
 */
class HttpClient {
public:
    /**
     * @brief Construct HTTP client with rate limiter
     * @param rate_limiter Reference to rate limiter (must outlive HttpClient)
     * @param logger Reference to logger
     */
    HttpClient(RateLimiter& rate_limiter, ILogger& logger);
    
    /**
     * @brief Destructor cleans up CURL handle
     */
    ~HttpClient();
    
    // Non-copyable (owns CURL handle)
    HttpClient(const HttpClient&) = delete;
    HttpClient& operator=(const HttpClient&) = delete;
    
    // Movable
    HttpClient(HttpClient&&) noexcept;
    HttpClient& operator=(HttpClient&&) noexcept;
    
    /**
     * @brief Perform GET request for JSON/text data
     * 
     * Automatically:
     * - Waits for rate limits via RateLimiter
     * - Retries on transient failures (5xx, network errors)
     * - Parses response headers for rate limit tracking
     * - Throws exceptions on failure
     * 
     * @param url URL to request
     * @param headers Additional HTTP headers
     * @return HttpResponse with status, body, and headers
     * @throws NetworkException on CURL errors
     * @throws ApiException on HTTP errors
     * @throws RateLimitException on 429
     */
    HttpResponse get(const std::string& url, const Headers& headers = {});
    
    /**
     * @brief Download file with progress tracking
     * 
     * Streams response directly to file (memory-efficient for large files).
     * 
     * CRITICAL: Sets CURLOPT_NOPROGRESS = 0 to enable progress callbacks.
     * Progress callbacks are throttled to max 10 updates/second.
     * 
     * @param url URL to download
     * @param output_path Where to save the file
     * @param headers Additional HTTP headers
     * @param progress_callback Optional progress callback
     * @return true on success
     * @throws NetworkException on CURL errors
     * @throws ApiException on HTTP errors
     */
    bool downloadFile(const std::string& url, 
                     const std::filesystem::path& output_path,
                     const Headers& headers = {},
                     ProgressCallback progress_callback = nullptr);
    
    /**
     * @brief Set retry policy for requests
     */
    void setRetryPolicy(const RetryPolicy& policy) { retry_policy_ = policy; }
    
    /**
     * @brief Set connection timeout in seconds
     */
    void setTimeout(int seconds) { timeout_seconds_ = seconds; }

private:
    CURL* curl_handle_;
    RateLimiter& rate_limiter_;
    ILogger& logger_;
    RetryPolicy retry_policy_;
    int timeout_seconds_ = 30;
    
    // CURL callbacks
    static size_t writeCallback(void* contents, size_t size, size_t nmemb, void* userp);
    static size_t headerCallback(char* buffer, size_t size, size_t nitems, void* userp);
    static int progressCallback(void* clientp, curl_off_t dltotal, curl_off_t dlnow,
                                curl_off_t ultotal, curl_off_t ulnow);
    
    // Setup CURL for a request
    void setupRequest(const std::string& url, const Headers& headers, 
                     struct curl_slist** curl_headers);
    
    // Parse response headers into map
    void parseHeader(const std::string& header_line, 
                    std::map<std::string, std::string>& headers);
    
    // Determine if a status code should be retried
    bool shouldRetry(long status_code, int curl_code) const;
    
    // Calculate retry delay with exponential backoff
    int calculateRetryDelay(int attempt) const;
    
    // Throw appropriate exception based on response
    void throwOnError(long status_code, const std::string& url, 
                     const std::string& response_body);
};

/**
 * @brief RAII wrapper for curl_global_init/cleanup
 * 
 * Put one of these in main() to handle CURL global state.
 * Must be constructed before any HttpClient instances.
 */
struct CurlGlobal {
    CurlGlobal() {
        curl_global_init(CURL_GLOBAL_ALL);
    }
    
    ~CurlGlobal() {
        curl_global_cleanup();
    }
    
    // Non-copyable, non-movable
    CurlGlobal(const CurlGlobal&) = delete;
    CurlGlobal& operator=(const CurlGlobal&) = delete;
};

} // namespace modular

#endif // MODULAR_HTTPCLIENT_H
