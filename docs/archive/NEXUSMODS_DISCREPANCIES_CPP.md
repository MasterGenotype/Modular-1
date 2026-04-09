# NexusMods.cpp API Discrepancies & Recommendations

## Overview

Review of `NexusMods.cpp` against official NexusMods API documentation reveals several discrepancies and missing features that could cause rate limiting issues or API errors.

## Critical Issues

### 1. Rate Limiting - CRITICAL ⚠️

**Location**: Lines 315, 354

**Current Implementation**:
```cpp
std::this_thread::sleep_for(std::chrono::seconds(1));  // Fixed 1-second delay
```

**Documentation Requirements**:
- **Daily limit**: 20,000 requests per 24 hours
- **Hourly limit**: 500 requests per hour (after daily limit)
- **Resets**: Daily at 00:00 GMT, Hourly at XX:00:00

**Problems**:
1. **Too Aggressive**: 1 second = 3600 requests/hour (7x over hourly limit)
2. **No Header Checking**: Doesn't read rate limit headers from responses
3. **No Adaptive Throttling**: Should slow down as limits approach
4. **No 429 Handling**: Doesn't detect/handle rate limit exceeded errors

**Response Headers** (per documentation):
```
X-RL-Daily-Limit: 20000
X-RL-Daily-Remaining: 15000
X-RL-Daily-Reset: 1643673600
X-RL-Hourly-Limit: 500
X-RL-Hourly-Remaining: 250
X-RL-Hourly-Reset: 1643670000
```

**Impact**:
- Users will hit rate limits frequently
- No graceful handling when limits exceeded
- Downloads will fail without clear error messages

**Recommended Fix**:
1. Add header extraction to `http_get()`
2. Create `RateLimitManager` class to track limits
3. Check remaining requests before each API call
4. Increase delay to **2-3 seconds** minimum between requests
5. Implement exponential backoff on 429 responses
6. Log rate limit status periodically

### 2. HTTP 429 Response Handling - MISSING ❌

**Current State**: No specific handling for 429 (Too Many Requests)

**Documentation**: 
- Server returns 429 with JSON error when rate limited
- Response includes `Retry-After` header or error message

**Current Error Handling**:
```cpp
} else {
    // ignore HTTP failure
}
```

**Recommended Fix**:
```cpp
if (resp.status_code == 429) {
    // Parse Retry-After header
    // Calculate wait time until reset
    // Log clear message to user
    // Pause and retry
    std::cerr << "[ERROR] Rate limit exceeded. Waiting until reset..." << std::endl;
    // Sleep until hourly/daily reset
}
```

### 3. Response Header Extraction - MISSING ❌

**Location**: `http_get()` function (lines 36-64)

**Current**:
```cpp
HttpResponse response { 0, "", {} };  // Headers always empty
```

**Problem**: The function never populates the `headers` field, making it impossible to:
- Check rate limits
- Read error details
- Parse retry-after times
- Validate content-type

**Recommended Fix**:
```cpp
// Add header callback to curl
struct HeaderData {
    std::map<std::string, std::string> headers;
};

static size_t HeaderCallback(char* buffer, size_t size, size_t nitems, void* userdata) {
    size_t totalSize = size * nitems;
    auto* data = static_cast<HeaderData*>(userdata);
    
    std::string header(buffer, totalSize);
    size_t colon = header.find(':');
    if (colon != std::string::npos) {
        std::string name = header.substr(0, colon);
        std::string value = header.substr(colon + 2); // Skip ": "
        // Trim newlines
        value.erase(value.find_last_not_of("\r\n") + 1);
        data->headers[name] = value;
    }
    return totalSize;
}

// In http_get():
HeaderData header_data;
curl_easy_setopt(curl, CURLOPT_HEADERFUNCTION, HeaderCallback);
curl_easy_setopt(curl, CURLOPT_HEADERDATA, &header_data);

// After perform:
response.headers = header_data.headers;
```

## Medium Priority Issues

### 4. Download Link Expiry Parameter ⚠️

**Location**: Line 332

**Current**:
```cpp
<< "/download_link.json?expires=999999";  // ~11.5 days
```

**Issue**: 
- Hardcoded to 999999 seconds (11.5 days)
- Documentation doesn't specify limits
- May cause issues with link expiration
- Not configurable

**Recommended Fix**:
- Use a reasonable default (e.g., 86400 = 24 hours)
- Make configurable via Config
- Or omit parameter to use API default

### 5. API Error Response Parsing - INCOMPLETE ⚠️

**Current**: Generic exception catching with no error detail extraction

**Documentation Error Format**:
```json
{
  "code": 403,
  "message": "You don't have permission to get download links..."
}
```

**Current Handling**:
```cpp
} catch (const std::exception&) {
    // ignore JSON error
}
```

**Problem**: Users don't see helpful error messages from API

**Recommended Fix**:
```cpp
} catch (const json::exception& e) {
    std::cerr << "[ERROR] JSON parse error: " << e.what() << std::endl;
    if (resp.status_code >= 400) {
        try {
            json error = json::parse(resp.body);
            if (error.contains("message")) {
                std::cerr << "[ERROR] API: " << error["message"] << std::endl;
            }
        } catch (...) {}
    }
}
```

### 6. SSL Certificate Validation - CORRECT ✅

**Current**:
```cpp
curl_easy_setopt(curl, CURLOPT_SSL_VERIFYPEER, 1L);
curl_easy_setopt(curl, CURLOPT_SSL_VERIFYHOST, 2L);
```

**Status**: Correct per best practices

## Low Priority Issues

### 7. Content-Type Validation - MISSING

**Current**: No validation that response is JSON

**Recommended**: Check `Content-Type: application/json` header

### 8. User-Agent Header - MISSING

**Current**: No User-Agent header sent

**Recommended**: 
```cpp
headers.push_back("User-Agent: Modular/1.0.0");
```

## Implementation Priority

### Phase 1 - Critical (Immediate)
1. ✅ Add header extraction to `http_get()`
2. ✅ Implement rate limit tracking
3. ✅ Add 429 response handling
4. ✅ Increase base delay to 2-3 seconds

### Phase 2 - Important
5. ✅ Parse API error messages
6. ✅ Make download expiry configurable
7. ✅ Add retry logic for transient failures

### Phase 3 - Nice to Have
8. ⬜ Add User-Agent header
9. ⬜ Validate Content-Type
10. ⬜ Log rate limit status

## Example Rate Limit Manager

```cpp
class RateLimitManager {
private:
    int daily_remaining;
    int hourly_remaining;
    time_t daily_reset;
    time_t hourly_reset;

public:
    void updateFromHeaders(const Headers& headers) {
        if (headers.count("X-RL-Daily-Remaining")) {
            daily_remaining = std::stoi(headers.at("X-RL-Daily-Remaining"));
        }
        if (headers.count("X-RL-Hourly-Remaining")) {
            hourly_remaining = std::stoi(headers.at("X-RL-Hourly-Remaining"));
        }
        // Parse reset times...
    }
    
    bool shouldThrottle() const {
        return daily_remaining < 1000 || hourly_remaining < 50;
    }
    
    std::chrono::seconds getRecommendedDelay() const {
        if (daily_remaining < 100 || hourly_remaining < 10) {
            return std::chrono::seconds(10);
        } else if (shouldThrottle()) {
            return std::chrono::seconds(5);
        }
        return std::chrono::seconds(2);
    }
    
    void waitUntilReset() {
        time_t now = time(nullptr);
        time_t reset_time = (hourly_remaining == 0) ? hourly_reset : daily_reset;
        
        if (reset_time > now) {
            std::this_thread::sleep_until(
                std::chrono::system_clock::from_time_t(reset_time)
            );
        }
    }
};
```

## Testing Recommendations

1. **Test Rate Limiting**:
   - Monitor actual request rate
   - Verify proper handling of 429 responses
   - Check header parsing

2. **Test Error Scenarios**:
   - Invalid API key (401)
   - Non-existent mod (404)
   - Rate limit exceeded (429)
   - Network failures

3. **Test with Real Usage**:
   - Download 100+ mods
   - Monitor rate limit headers
   - Verify stays under 500/hour

## References

- Rate Limiting: `~/Games/WarpAI-WorkSpace/docs/nexusmods/help_API-rate-limiting.html`
- API Docs: FluentNexus_README.md, node-nexus-api documentation
- Current quota: Can be checked via `GET /v1/users/validate.json` response headers
