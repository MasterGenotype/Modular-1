#pragma once

#include "Types.h"
#include "IRequest.h"
#include "IResponse.h"
#include "IRetryConfig.h"
#include <functional>
#include <future>

namespace modular::fluent {

/// Interface for controlling how requests are dispatched and retried.
/// Only one coordinator can be active on a client at a time.
/// Use filters for additional cross-cutting concerns.
///
/// Mirrors FluentHttpClient's IRequestCoordinator interface.
///
/// This is the integration point for:
/// - Custom retry logic (e.g., Polly-style policies)
/// - Circuit breakers
/// - Request queuing
/// - Rate limiting coordination
class IRequestCoordinator {
public:
    virtual ~IRequestCoordinator() = default;

    /// Execute an HTTP request with coordination logic.
    ///
    /// @param request The request to execute
    /// @param dispatcher Function that performs the actual HTTP request.
    ///        Call this to send the request; may be called multiple times for retries.
    /// @return The final response (after any retries)
    ///
    /// @note The coordinator owns the retry loop - dispatcher just sends one request
    /// @note Coordinator should handle exceptions from dispatcher appropriately
    virtual std::future<ResponsePtr> executeAsync(
        IRequest& request,
        std::function<std::future<ResponsePtr>(IRequest&)> dispatcher
    ) = 0;

    /// Get a human-readable name for this coordinator (for logging)
    virtual std::string name() const { return "IRequestCoordinator"; }
};

/// Shared pointer type for coordinators
using CoordinatorPtr = std::shared_ptr<IRequestCoordinator>;

//=============================================================================
// Default Coordinator Implementations
//=============================================================================

/// Coordinator that passes requests through without modification
class PassThroughCoordinator : public IRequestCoordinator {
public:
    std::future<ResponsePtr> executeAsync(
        IRequest& request,
        std::function<std::future<ResponsePtr>(IRequest&)> dispatcher
    ) override {
        return dispatcher(request);
    }

    std::string name() const override { return "PassThroughCoordinator"; }
};

} // namespace modular::fluent
