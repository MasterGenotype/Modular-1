#pragma once

/// @file Fluent.h
/// @brief Master include file for the Fluent HTTP Client library
///
/// This file includes all public interfaces for the fluent HTTP client.
/// Include this single header to access the complete API.
///
/// @code
/// #include <fluent/Fluent.h>
/// using namespace modular::fluent;
///
/// auto client = createFluentClient("https://api.example.com");
/// auto result = client->getAsync("users")->as<std::vector<User>>();
/// @endcode

#include "Types.h"
#include "Exceptions.h"
#include "IResponse.h"
#include "IBodyBuilder.h"
#include "IRequest.h"
#include "IHttpFilter.h"
#include "IRetryConfig.h"
#include "IRequestCoordinator.h"
#include "IRateLimiter.h"
#include "IFluentClient.h"

namespace modular::fluent {

/// Library version
constexpr const char* VERSION = "1.0.0";

/// Library version as components
constexpr int VERSION_MAJOR = 1;
constexpr int VERSION_MINOR = 0;
constexpr int VERSION_PATCH = 0;

} // namespace modular::fluent
