#pragma once

#include "Types.h"
#include <memory>
#include <vector>
#include <algorithm>

namespace modular::fluent {

// Forward declarations
class IRequest;
class IResponse;

/// Middleware interface for intercepting and modifying HTTP requests and responses.
/// Filters are executed in order: OnRequest is called before the request is sent,
/// and OnResponse is called after the response is received.
///
/// This mirrors FluentHttpClient's IHttpFilter interface.
///
/// Common use cases:
/// - Authentication (add headers on request)
/// - Logging (log requests and responses)
/// - Error handling (throw on error status codes)
/// - Rate limiting (check/update limits)
/// - Caching (return cached response, skip request)
class IHttpFilter {
public:
    virtual ~IHttpFilter() = default;

    /// Called just before the HTTP request is sent.
    /// Implementations can modify the outgoing request (add headers, change URL, etc.)
    ///
    /// @param request The request about to be sent (mutable)
    ///
    /// @note Throwing an exception here will abort the request
    /// @note Filters are called in the order they were added to the client
    virtual void onRequest(IRequest& request) = 0;

    /// Called just after the HTTP response is received.
    /// Implementations can inspect or modify the response, or throw exceptions.
    ///
    /// @param response The response received (mutable)
    /// @param httpErrorAsException Whether HTTP errors (4xx/5xx) should throw
    ///        This reflects the client's `ignoreHttpErrors` setting
    ///
    /// @note Throwing an exception here will propagate to the caller
    /// @note Filters are called in reverse order (last added = first called)
    virtual void onResponse(IResponse& response, bool httpErrorAsException) = 0;

    /// Get a human-readable name for this filter (for logging/debugging)
    virtual std::string name() const { return "IHttpFilter"; }

    /// Get the priority of this filter (lower = earlier execution)
    /// Default filters use priority 1000. Use lower values to run earlier.
    virtual int priority() const { return 1000; }
};

/// Shared pointer type for filters (filters may be shared across requests)
using FilterPtr = std::shared_ptr<IHttpFilter>;

//=============================================================================
// Filter Collection Helper
//=============================================================================

/// A collection of filters with helper methods for management
class FilterCollection {
public:
    /// Add a filter to the collection
    void add(FilterPtr filter) {
        filters_.push_back(std::move(filter));
        sortByPriority();
    }

    /// Remove a specific filter instance
    bool remove(const FilterPtr& filter) {
        auto it = std::find(filters_.begin(), filters_.end(), filter);
        if (it != filters_.end()) {
            filters_.erase(it);
            return true;
        }
        return false;
    }

    /// Remove all filters of a specific type
    template<typename T>
    size_t removeAll() {
        size_t removed = 0;
        filters_.erase(
            std::remove_if(filters_.begin(), filters_.end(),
                [&removed](const FilterPtr& f) {
                    if (dynamic_cast<T*>(f.get())) {
                        ++removed;
                        return true;
                    }
                    return false;
                }),
            filters_.end()
        );
        return removed;
    }

    /// Check if collection contains a filter of a specific type
    template<typename T>
    bool contains() const {
        for (const auto& f : filters_) {
            if (dynamic_cast<T*>(f.get())) return true;
        }
        return false;
    }

    /// Get all filters (for iteration)
    const std::vector<FilterPtr>& all() const { return filters_; }

    /// Clear all filters
    void clear() { filters_.clear(); }

    /// Get number of filters
    size_t size() const { return filters_.size(); }

    /// Check if empty
    bool empty() const { return filters_.empty(); }

private:
    void sortByPriority() {
        std::sort(filters_.begin(), filters_.end(),
            [](const FilterPtr& a, const FilterPtr& b) {
                return a->priority() < b->priority();
            });
    }

    std::vector<FilterPtr> filters_;
};

} // namespace modular::fluent
