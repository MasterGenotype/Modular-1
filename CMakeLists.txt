cmake_minimum_required(VERSION 3.20)
project(Modular VERSION 1.0.0 LANGUAGES CXX)

# Set C++ standard
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Enable folders in IDEs
set_property(GLOBAL PROPERTY USE_FOLDERS ON)

# Find required packages
find_package(CURL REQUIRED)
find_package(nlohmann_json 3.11 REQUIRED)
find_package(OpenSSL REQUIRED)

#==============================================================================
# Core Library (libmodular-core)
#==============================================================================

add_library(modular-core STATIC
    # Core business logic
    src/core/HttpClient.cpp
    src/core/RateLimiter.cpp
    src/core/Config.cpp
    src/core/Utils.cpp
    src/core/Database.cpp
    src/core/NexusMods.cpp
    src/core/GameBanana.cpp
    src/core/Rename.cpp
    src/core/HtmlParser.cpp
    src/core/TrackingValidator.cpp
    
    # Headers
    include/core/HttpClient.h
    include/core/RateLimiter.h
    include/core/Config.h
    include/core/Utils.h
    include/core/Database.h
    include/core/ILogger.h
    include/core/Exceptions.h
    include/core/NexusMods.h
    include/core/GameBanana.h
    include/core/Rename.h
    include/core/HtmlParser.h
    include/core/TrackingValidator.h
)

target_include_directories(modular-core 
    PUBLIC 
        include
    PRIVATE
        src/core
)

target_link_libraries(modular-core 
    PUBLIC 
        CURL::libcurl
        nlohmann_json::nlohmann_json
        OpenSSL::Crypto
)

#==============================================================================
# CLI Executable (modular-cli)
#==============================================================================

add_executable(modular-cli
    src/cli/main.cpp
    src/cli/LiveUI.cpp
    include/cli/LiveUI.h
)

target_include_directories(modular-cli
    PRIVATE
        ${CMAKE_SOURCE_DIR}/include/cli
        ${CMAKE_SOURCE_DIR}/include/core
)

target_link_libraries(modular-cli 
    PRIVATE 
        modular-core
)

# Compiler warnings
if (MSVC)
    target_compile_options(modular-core PRIVATE /W4)
    target_compile_options(modular-cli PRIVATE /W4)
else()
    target_compile_options(modular-core PRIVATE -Wall -Wextra -Wpedantic)
    target_compile_options(modular-cli PRIVATE -Wall -Wextra -Wpedantic)
endif()

#==============================================================================
# Testing (optional)
#==============================================================================

option(BUILD_TESTS "Build unit tests" ON)

if(BUILD_TESTS)
    enable_testing()
    
    # Fetch Catch2 v3
    include(FetchContent)
    FetchContent_Declare(
        Catch2
        GIT_REPOSITORY https://github.com/catchorg/Catch2.git
        GIT_TAG v3.5.2
    )
    FetchContent_MakeAvailable(Catch2)
    
    # Test executable
    add_executable(modular-tests
        tests/test_main.cpp
        tests/test_config.cpp
        tests/test_utils.cpp
        tests/test_database.cpp
    )
    
    target_link_libraries(modular-tests
        PRIVATE
            modular-core
            Catch2::Catch2WithMain
    )
    
    target_include_directories(modular-tests
        PRIVATE
            ${CMAKE_SOURCE_DIR}/include/core
    )
    
    # Discover tests
    list(APPEND CMAKE_MODULE_PATH ${catch2_SOURCE_DIR}/extras)
    include(CTest)
    include(Catch)
    catch_discover_tests(modular-tests)
endif()

# Install targets
install(TARGETS modular-cli DESTINATION bin)
