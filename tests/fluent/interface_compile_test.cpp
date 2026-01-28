/**
 * @file interface_compile_test.cpp
 * @brief Compilation test to verify all fluent interfaces are well-formed
 * 
 * This test verifies that:
 * - All headers compile without errors
 * - Static assertions pass
 * - Basic type instantiation works
 * - Exception hierarchy is correct
 */

#include <fluent/Fluent.h>
#include <cassert>
#include <iostream>

using namespace modular::fluent;

// Verify all types are complete
static_assert(sizeof(RequestOptions) > 0);
static_assert(sizeof(RetryPolicy) > 0);
static_assert(sizeof(RateLimitStatus) > 0);
static_assert(sizeof(RequestBody) > 0);

// Verify enum values
static_assert(to_string(HttpMethod::GET) == "GET");
static_assert(to_string(HttpMethod::POST) == "POST");
static_assert(to_string(HttpMethod::PUT) == "PUT");
static_assert(to_string(HttpMethod::DELETE) == "DELETE");

// Verify status utilities
static_assert(is_success_status(200));
static_assert(is_success_status(201));
static_assert(is_success_status(299));
static_assert(!is_success_status(199));
static_assert(!is_success_status(300));
static_assert(!is_success_status(404));
static_assert(!is_success_status(500));

static_assert(categorize_status(100) == StatusCategory::Informational);
static_assert(categorize_status(200) == StatusCategory::Success);
static_assert(categorize_status(301) == StatusCategory::Redirection);
static_assert(categorize_status(404) == StatusCategory::ClientError);
static_assert(categorize_status(500) == StatusCategory::ServerError);

// Verify exception hierarchy
static_assert(std::is_base_of_v<std::exception, FluentException>);
static_assert(std::is_base_of_v<FluentException, NetworkException>);
static_assert(std::is_base_of_v<FluentException, ApiException>);
static_assert(std::is_base_of_v<ApiException, RateLimitException>);
static_assert(std::is_base_of_v<ApiException, AuthException>);
static_assert(std::is_base_of_v<FluentException, ParseException>);
static_assert(std::is_base_of_v<FluentException, ConfigurationException>);

int main() {
    std::cout << "Fluent HTTP Client v" << VERSION << std::endl;
    std::cout << "Testing interface compilation..." << std::endl;

    // Test exception construction
    try {
        throw ApiException("Test error", 404, "Not Found", {}, "");
    } catch (const std::exception& e) {
        assert(std::string(e.what()) == "Test error");
    }

    // Test RateLimitException
    try {
        throw RateLimitException("Rate limited", {}, "", std::chrono::seconds{60});
    } catch (const RateLimitException& e) {
        assert(e.statusCode() == 429);
        assert(e.retryAfter() == std::chrono::seconds{60});
    }

    // Test AuthException
    try {
        throw AuthException("Unauthorized", 401, {}, "");
    } catch (const AuthException& e) {
        assert(e.statusCode() == 401);
        assert(e.reason() == AuthException::Reason::Unauthorized);
    }

    // Test NetworkException
    try {
        throw NetworkException("Connection failed", NetworkException::Reason::ConnectionFailed);
    } catch (const NetworkException& e) {
        assert(!e.isTimeout());
    }

    try {
        throw NetworkException("Timeout", NetworkException::Reason::Timeout);
    } catch (const NetworkException& e) {
        assert(e.isTimeout());
    }

    // Test FilterCollection
    FilterCollection filters;
    assert(filters.empty());
    assert(filters.size() == 0);

    // Test RequestOptions
    RequestOptions opts;
    assert(!opts.ignoreHttpErrors.has_value());
    opts.ignoreHttpErrors = true;
    assert(opts.ignoreHttpErrors.value() == true);

    // Test RetryPolicy
    RetryPolicy policy;
    assert(policy.maxRetries == 3);
    assert(policy.exponentialBackoff == true);

    // Test RateLimitStatus
    RateLimitStatus status;
    status.dailyRemaining = 100;
    status.hourlyRemaining = 50;
    assert(status.canRequest());
    
    status.dailyRemaining = 0;
    assert(!status.canRequest());

    // Test RequestBody
    RequestBody body;
    assert(body.empty());
    
    RequestBody body2("test content", "text/plain");
    assert(!body2.empty());
    assert(body2.size() == 12); // "test content"
    assert(body2.contentType == "text/plain");

    // Test ServerErrorRetryConfig
    ServerErrorRetryConfig serverRetry;
    assert(serverRetry.maxRetries() == 3);
    assert(serverRetry.shouldRetry(500, false));
    assert(serverRetry.shouldRetry(503, false));
    assert(!serverRetry.shouldRetry(404, false));
    assert(serverRetry.shouldRetry(0, true)); // timeout

    // Test RateLimitRetryConfig
    RateLimitRetryConfig rateLimitRetry;
    assert(rateLimitRetry.shouldRetry(429, false));
    assert(!rateLimitRetry.shouldRetry(500, false));

    // Test TimeoutRetryConfig
    TimeoutRetryConfig timeoutRetry;
    assert(timeoutRetry.shouldRetry(0, true));
    assert(!timeoutRetry.shouldRetry(500, false));

    std::cout << "All interface compilation tests passed!" << std::endl;
    return 0;
}
