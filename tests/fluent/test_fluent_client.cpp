#include <catch2/catch_test_macros.hpp>
#include <catch2/matchers/catch_matchers_string.hpp>

#include <fluent/Fluent.h>
#include <fluent/IHttpFilter.h>
#include <fluent/IRetryConfig.h>

using namespace modular::fluent;

// Mock filter for testing
class MockFilter : public IHttpFilter {
public:
    mutable int requestCount = 0;
    mutable int responseCount = 0;
    mutable std::string lastUrl;
    mutable HttpMethod lastMethod = HttpMethod::GET;

    void onRequest(IRequest& request) override {
        ++requestCount;
        lastUrl = request.url();
        lastMethod = request.method();
    }

    void onResponse(IResponse& /*response*/, bool /*httpErrorAsException*/) override {
        ++responseCount;
    }

    std::string name() const override { return "MockFilter"; }
    int priority() const override { return 500; }
};

TEST_CASE("Fluent Types", "[fluent][types]") {
    SECTION("HttpMethod to_string") {
        CHECK(to_string(HttpMethod::GET) == "GET");
        CHECK(to_string(HttpMethod::POST) == "POST");
        CHECK(to_string(HttpMethod::PUT) == "PUT");
        CHECK(to_string(HttpMethod::PATCH) == "PATCH");
        CHECK(to_string(HttpMethod::DELETE) == "DELETE");
        CHECK(to_string(HttpMethod::HEAD) == "HEAD");
        CHECK(to_string(HttpMethod::OPTIONS) == "OPTIONS");
    }

    SECTION("categorize_status") {
        CHECK(categorize_status(100) == StatusCategory::Informational);
        CHECK(categorize_status(200) == StatusCategory::Success);
        CHECK(categorize_status(201) == StatusCategory::Success);
        CHECK(categorize_status(301) == StatusCategory::Redirection);
        CHECK(categorize_status(404) == StatusCategory::ClientError);
        CHECK(categorize_status(500) == StatusCategory::ServerError);
    }

    SECTION("is_success_status") {
        CHECK(is_success_status(200));
        CHECK(is_success_status(201));
        CHECK(is_success_status(204));
        CHECK_FALSE(is_success_status(400));
        CHECK_FALSE(is_success_status(500));
    }
}

TEST_CASE("FilterCollection", "[fluent][filters]") {
    FilterCollection filters;

    SECTION("Empty collection") {
        CHECK(filters.empty());
        CHECK(filters.size() == 0);
    }

    SECTION("Add and retrieve filters") {
        auto filter1 = std::make_shared<MockFilter>();
        auto filter2 = std::make_shared<MockFilter>();

        filters.add(filter1);
        CHECK(filters.size() == 1);
        CHECK_FALSE(filters.empty());

        filters.add(filter2);
        CHECK(filters.size() == 2);

        CHECK(filters.contains<MockFilter>());
    }

    SECTION("Remove filter") {
        auto filter = std::make_shared<MockFilter>();
        filters.add(filter);
        
        CHECK(filters.remove(filter));
        CHECK(filters.empty());
        
        // Removing non-existent filter returns false
        CHECK_FALSE(filters.remove(filter));
    }

    SECTION("Clear filters") {
        filters.add(std::make_shared<MockFilter>());
        filters.add(std::make_shared<MockFilter>());
        
        filters.clear();
        CHECK(filters.empty());
    }
}

TEST_CASE("Retry Configs", "[fluent][retry]") {
    SECTION("ServerErrorRetryConfig") {
        ServerErrorRetryConfig config(3);

        CHECK(config.maxRetries() == 3);
        CHECK(config.shouldRetry(500, false));
        CHECK(config.shouldRetry(503, false));
        CHECK(config.shouldRetry(0, true));  // Timeout
        CHECK_FALSE(config.shouldRetry(200, false));
        CHECK_FALSE(config.shouldRetry(404, false));
    }

    SECTION("RateLimitRetryConfig") {
        RateLimitRetryConfig config(2);

        CHECK(config.maxRetries() == 2);
        CHECK(config.shouldRetry(429, false));
        CHECK_FALSE(config.shouldRetry(500, false));
        CHECK_FALSE(config.shouldRetry(200, false));
    }

    SECTION("TimeoutRetryConfig") {
        TimeoutRetryConfig config(2, std::chrono::milliseconds{500});

        CHECK(config.maxRetries() == 2);
        CHECK(config.shouldRetry(0, true));
        CHECK_FALSE(config.shouldRetry(0, false));
        CHECK_FALSE(config.shouldRetry(500, false));
    }

    SECTION("Exponential backoff") {
        ServerErrorRetryConfig config(3, std::chrono::milliseconds{100}, std::chrono::milliseconds{1000});

        auto delay1 = config.getDelay(1, 500);
        auto delay2 = config.getDelay(2, 500);
        auto delay3 = config.getDelay(3, 500);

        CHECK(delay1 == std::chrono::milliseconds{100});
        CHECK(delay2 == std::chrono::milliseconds{200});
        CHECK(delay3 == std::chrono::milliseconds{400});
    }
}

TEST_CASE("RequestOptions", "[fluent][options]") {
    RequestOptions opts;

    SECTION("Default values are empty") {
        CHECK_FALSE(opts.ignoreHttpErrors.has_value());
        CHECK_FALSE(opts.timeout.has_value());
    }

    SECTION("Can set values") {
        opts.ignoreHttpErrors = true;
        opts.timeout = std::chrono::seconds{30};

        CHECK(opts.ignoreHttpErrors.value() == true);
        CHECK(opts.timeout.value() == std::chrono::seconds{30});
    }
}

TEST_CASE("RequestBody", "[fluent][body]") {
    SECTION("Empty body") {
        RequestBody body;
        CHECK(body.empty());
        CHECK(body.size() == 0);
    }

    SECTION("String body") {
        RequestBody body("hello world", "text/plain");
        CHECK_FALSE(body.empty());
        CHECK(body.size() == 11);
        CHECK(body.contentType == "text/plain");
    }

    SECTION("Binary body") {
        std::vector<uint8_t> data = {0x00, 0x01, 0x02, 0x03};
        RequestBody body(data, "application/octet-stream");
        CHECK(body.size() == 4);
        CHECK(body.contentType == "application/octet-stream");
    }
}

TEST_CASE("Exceptions", "[fluent][exceptions]") {
    SECTION("NetworkException") {
        NetworkException ex("Connection failed", NetworkException::Reason::ConnectionFailed);
        CHECK(ex.reason() == NetworkException::Reason::ConnectionFailed);
        CHECK_FALSE(ex.isTimeout());

        NetworkException timeout("Timeout", NetworkException::Reason::Timeout);
        CHECK(timeout.isTimeout());
    }

    SECTION("ApiException") {
        ApiException ex("Not Found", 404, "Not Found", {{"Content-Type", "application/json"}}, "{}");
        CHECK(ex.statusCode() == 404);
        CHECK(ex.isClientError());
        CHECK_FALSE(ex.isServerError());
    }

    SECTION("RateLimitException") {
        RateLimitException ex("Rate limited", {}, "", std::chrono::seconds{60});
        CHECK(ex.statusCode() == 429);
        CHECK(ex.retryAfter() == std::chrono::seconds{60});
    }
}

TEST_CASE("RateLimitStatus", "[fluent][ratelimit]") {
    RateLimitStatus status;

    SECTION("Default values") {
        CHECK(status.dailyRemaining == 0);
        CHECK(status.hourlyRemaining == 0);
        CHECK_FALSE(status.canRequest());
    }

    SECTION("Can request when limits available") {
        status.dailyRemaining = 100;
        status.hourlyRemaining = 50;
        CHECK(status.canRequest());
    }

    SECTION("Cannot request when daily exhausted") {
        status.dailyRemaining = 0;
        status.hourlyRemaining = 50;
        CHECK_FALSE(status.canRequest());
    }

    SECTION("Cannot request when hourly exhausted") {
        status.dailyRemaining = 100;
        status.hourlyRemaining = 0;
        CHECK_FALSE(status.canRequest());
    }
}
