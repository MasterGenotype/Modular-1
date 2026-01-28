#include "HttpClient.h"
#include "Exceptions.h"
#include <thread>
#include <chrono>
#include <fstream>
#include <sstream>
#include <algorithm>

namespace modular {

// Progress callback data
struct ProgressData {
    ProgressCallback user_callback;
    std::chrono::steady_clock::time_point last_update;
    size_t last_downloaded = 0;
};

HttpClient::HttpClient(RateLimiter& rate_limiter, ILogger& logger)
    : curl_handle_(curl_easy_init()), rate_limiter_(rate_limiter), logger_(logger) {
    
    if (!curl_handle_) {
        throw NetworkException("Failed to initialize CURL handle");
    }
    
    // Set default options
    curl_easy_setopt(curl_handle_, CURLOPT_SSL_VERIFYPEER, 1L);
    curl_easy_setopt(curl_handle_, CURLOPT_SSL_VERIFYHOST, 2L);
    curl_easy_setopt(curl_handle_, CURLOPT_FOLLOWLOCATION, 1L);
    curl_easy_setopt(curl_handle_, CURLOPT_MAXREDIRS, 5L);
}

HttpClient::~HttpClient() {
    if (curl_handle_) {
        curl_easy_cleanup(curl_handle_);
    }
}

HttpClient::HttpClient(HttpClient&& other) noexcept
    : curl_handle_(other.curl_handle_),
      rate_limiter_(other.rate_limiter_),
      logger_(other.logger_),
      retry_policy_(other.retry_policy_),
      timeout_seconds_(other.timeout_seconds_) {
    other.curl_handle_ = nullptr;
}

HttpClient& HttpClient::operator=(HttpClient&& other) noexcept {
    if (this != &other) {
        if (curl_handle_) {
            curl_easy_cleanup(curl_handle_);
        }
        curl_handle_ = other.curl_handle_;
        retry_policy_ = other.retry_policy_;
        timeout_seconds_ = other.timeout_seconds_;
        other.curl_handle_ = nullptr;
    }
    return *this;
}

// Static CURL callbacks

size_t HttpClient::writeCallback(void* contents, size_t size, size_t nmemb, void* userp) {
    size_t total_size = size * nmemb;
    std::string* str = static_cast<std::string*>(userp);
    str->append(static_cast<char*>(contents), total_size);
    return total_size;
}

size_t HttpClient::headerCallback(char* buffer, size_t size, size_t nitems, void* userp) {
    size_t total_size = size * nitems;
    auto* headers = static_cast<std::map<std::string, std::string>*>(userp);
    
    std::string header_line(buffer, total_size);
    
    // Parse "Key: Value" format
    size_t colon_pos = header_line.find(':');
    if (colon_pos != std::string::npos) {
        std::string key = header_line.substr(0, colon_pos);
        std::string value = header_line.substr(colon_pos + 1);
        
        // Trim whitespace
        key.erase(0, key.find_first_not_of(" \t\r\n"));
        key.erase(key.find_last_not_of(" \t\r\n") + 1);
        value.erase(0, value.find_first_not_of(" \t\r\n"));
        value.erase(value.find_last_not_of(" \t\r\n") + 1);
        
        (*headers)[key] = value;
    }
    
    return total_size;
}

int HttpClient::progressCallback(void* clientp, curl_off_t dltotal, curl_off_t dlnow,
                                 curl_off_t /*ultotal*/, curl_off_t /*ulnow*/) {
    auto* progress_data = static_cast<ProgressData*>(clientp);
    
    if (!progress_data || !progress_data->user_callback) {
        return 0;  // Continue
    }
    
    // Throttle updates to max 10/second (100ms intervals)
    auto now = std::chrono::steady_clock::now();
    auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
        now - progress_data->last_update);
    
    if (elapsed.count() >= 100 || dlnow == dltotal) {
        progress_data->user_callback(static_cast<size_t>(dlnow), 
                                     static_cast<size_t>(dltotal));
        progress_data->last_update = now;
    }
    
    return 0;  // Return 0 to continue, non-zero to abort
}

void HttpClient::parseHeader(const std::string& header_line,
                             std::map<std::string, std::string>& headers) {
    size_t colon_pos = header_line.find(':');
    if (colon_pos != std::string::npos) {
        std::string key = header_line.substr(0, colon_pos);
        std::string value = header_line.substr(colon_pos + 1);
        
        // Trim
        key.erase(0, key.find_first_not_of(" \t"));
        key.erase(key.find_last_not_of(" \t\r\n") + 1);
        value.erase(0, value.find_first_not_of(" \t"));
        value.erase(value.find_last_not_of(" \t\r\n") + 1);
        
        headers[key] = value;
    }
}

bool HttpClient::shouldRetry(long status_code, int curl_code) const {
    // Retry on CURL errors (network failures, timeouts, etc.)
    if (curl_code != CURLE_OK) {
        return true;
    }
    
    // Retry on server errors (5xx)
    if (status_code >= 500 && status_code < 600) {
        return true;
    }
    
    // Special case: Retry on 429 (rate limit) - but caller should handle this
    if (status_code == 429) {
        return false;  // Don't retry here, throw RateLimitException instead
    }
    
    // DON'T retry on client errors (4xx)
    if (status_code >= 400 && status_code < 500) {
        return false;
    }
    
    return false;
}

int HttpClient::calculateRetryDelay(int attempt) const {
    if (!retry_policy_.exponential_backoff) {
        return retry_policy_.initial_delay_ms;
    }
    
    // Exponential backoff: 1s, 2s, 4s, 8s, 16s
    int delay = retry_policy_.initial_delay_ms * (1 << attempt);
    
    // Cap at max delay
    return std::min(delay, retry_policy_.max_delay_ms);
}

void HttpClient::throwOnError(long status_code, const std::string& url,
                              const std::string& response_body) {
    if (status_code >= 200 && status_code < 300) {
        return;  // Success
    }
    
    std::string snippet = response_body.substr(0, 500);
    
    if (status_code == 429) {
        RateLimitException ex("Rate limit exceeded", url);
        ex.setResponseSnippet(snippet);
        throw ex;
    }
    
    if (status_code == 401 || status_code == 403) {
        AuthException ex(status_code, 
            status_code == 401 ? "Authentication failed" : "Access forbidden", url);
        ex.setResponseSnippet(snippet);
        throw ex;
    }
    
    if (status_code >= 400 && status_code < 500) {
        ApiException ex(status_code, "Client error: " + std::to_string(status_code), url);
        ex.setResponseSnippet(snippet);
        throw ex;
    }
    
    if (status_code >= 500) {
        ApiException ex(status_code, "Server error: " + std::to_string(status_code), url);
        ex.setResponseSnippet(snippet);
        throw ex;
    }
    
    // Unknown error
    ApiException ex(status_code, "HTTP error: " + std::to_string(status_code), url);
    ex.setResponseSnippet(snippet);
    throw ex;
}

HttpResponse HttpClient::get(const std::string& url, const Headers& headers) {
    // Wait for rate limits
    rate_limiter_.waitIfNeeded();
    
    HttpResponse response;
    struct curl_slist* curl_headers = nullptr;
    
    int attempt = 0;
    while (attempt <= retry_policy_.max_retries) {
        // Reset response
        response.body.clear();
        response.headers.clear();
        response.status_code = 0;
        
        // Setup CURL
        curl_easy_setopt(curl_handle_, CURLOPT_URL, url.c_str());
        curl_easy_setopt(curl_handle_, CURLOPT_TIMEOUT, static_cast<long>(timeout_seconds_));
        curl_easy_setopt(curl_handle_, CURLOPT_WRITEFUNCTION, writeCallback);
        curl_easy_setopt(curl_handle_, CURLOPT_WRITEDATA, &response.body);
        curl_easy_setopt(curl_handle_, CURLOPT_HEADERFUNCTION, headerCallback);
        curl_easy_setopt(curl_handle_, CURLOPT_HEADERDATA, &response.headers);
        
        // Set custom headers
        if (curl_headers) {
            curl_slist_free_all(curl_headers);
            curl_headers = nullptr;
        }
        for (const auto& header : headers) {
            curl_headers = curl_slist_append(curl_headers, header.c_str());
        }
        if (curl_headers) {
            curl_easy_setopt(curl_handle_, CURLOPT_HTTPHEADER, curl_headers);
        }
        
        // Perform request
        CURLcode curl_code = curl_easy_perform(curl_handle_);
        curl_easy_getinfo(curl_handle_, CURLINFO_RESPONSE_CODE, &response.status_code);
        
        // Update rate limiter from response headers
        rate_limiter_.updateFromHeaders(response.headers);
        
        // Check for errors
        if (curl_code != CURLE_OK) {
            std::string error_msg = curl_easy_strerror(curl_code);
            logger_.warn("CURL error on attempt " + std::to_string(attempt + 1) + 
                        ": " + error_msg);
            
            if (attempt >= retry_policy_.max_retries) {
                curl_slist_free_all(curl_headers);
                throw NetworkException("Network error: " + error_msg, url, curl_code);
            }
            
            // Retry
            int delay_ms = calculateRetryDelay(attempt);
            logger_.info("Retrying in " + std::to_string(delay_ms) + "ms...");
            std::this_thread::sleep_for(std::chrono::milliseconds(delay_ms));
            attempt++;
            continue;
        }
        
        // Check HTTP status
        if (shouldRetry(response.status_code, CURLE_OK)) {
            logger_.warn("Retryable HTTP error " + std::to_string(response.status_code) +
                        " on attempt " + std::to_string(attempt + 1));
            
            if (attempt >= retry_policy_.max_retries) {
                curl_slist_free_all(curl_headers);
                throwOnError(response.status_code, url, response.body);
            }
            
            int delay_ms = calculateRetryDelay(attempt);
            std::this_thread::sleep_for(std::chrono::milliseconds(delay_ms));
            attempt++;
            continue;
        }
        
        // Check for errors
        if (response.status_code >= 400) {
            curl_slist_free_all(curl_headers);
            throwOnError(response.status_code, url, response.body);
        }
        
        // Success!
        curl_slist_free_all(curl_headers);
        return response;
    }
    
    // Shouldn't reach here
    curl_slist_free_all(curl_headers);
    throw NetworkException("Max retries exceeded", url);
}

bool HttpClient::downloadFile(const std::string& url,
                              const std::filesystem::path& output_path,
                              const Headers& headers,
                              ProgressCallback progress_callback) {
    // Wait for rate limits
    rate_limiter_.waitIfNeeded();
    
    // Open output file
    std::ofstream file(output_path, std::ios::binary);
    if (!file) {
        throw FileSystemException("Failed to open file for writing", output_path.string());
    }
    
    // Setup progress tracking
    ProgressData progress_data;
    progress_data.user_callback = progress_callback;
    progress_data.last_update = std::chrono::steady_clock::now();
    
    // Setup CURL
    std::string body;
    std::map<std::string, std::string> response_headers;
    
    curl_easy_setopt(curl_handle_, CURLOPT_URL, url.c_str());
    curl_easy_setopt(curl_handle_, CURLOPT_TIMEOUT, static_cast<long>(timeout_seconds_));
    curl_easy_setopt(curl_handle_, CURLOPT_WRITEFUNCTION, writeCallback);
    curl_easy_setopt(curl_handle_, CURLOPT_WRITEDATA, &body);
    curl_easy_setopt(curl_handle_, CURLOPT_HEADERFUNCTION, headerCallback);
    curl_easy_setopt(curl_handle_, CURLOPT_HEADERDATA, &response_headers);
    
    // CRITICAL: Enable progress callbacks
    if (progress_callback) {
        curl_easy_setopt(curl_handle_, CURLOPT_NOPROGRESS, 0L);  // MUST SET THIS
        curl_easy_setopt(curl_handle_, CURLOPT_XFERINFOFUNCTION, progressCallback);
        curl_easy_setopt(curl_handle_, CURLOPT_XFERINFODATA, &progress_data);
    } else {
        curl_easy_setopt(curl_handle_, CURLOPT_NOPROGRESS, 1L);
    }
    
    // Set custom headers
    struct curl_slist* curl_headers = nullptr;
    for (const auto& header : headers) {
        curl_headers = curl_slist_append(curl_headers, header.c_str());
    }
    if (curl_headers) {
        curl_easy_setopt(curl_handle_, CURLOPT_HTTPHEADER, curl_headers);
    }
    
    // Perform request
    CURLcode curl_code = curl_easy_perform(curl_handle_);
    
    long status_code = 0;
    curl_easy_getinfo(curl_handle_, CURLINFO_RESPONSE_CODE, &status_code);
    
    // Update rate limiter
    rate_limiter_.updateFromHeaders(response_headers);
    
    // Cleanup
    curl_slist_free_all(curl_headers);
    
    // Check for errors
    if (curl_code != CURLE_OK) {
        file.close();
        std::filesystem::remove(output_path);  // Clean up partial download
        throw NetworkException("Download failed: " + std::string(curl_easy_strerror(curl_code)),
                              url, curl_code);
    }
    
    if (status_code >= 400) {
        file.close();
        std::filesystem::remove(output_path);
        throwOnError(status_code, url, body);
    }
    
    // Write to file
    file.write(body.c_str(), body.size());
    file.close();
    
    if (!file) {
        std::filesystem::remove(output_path);
        throw FileSystemException("Failed to write file", output_path.string());
    }
    
    return true;
}

} // namespace modular
