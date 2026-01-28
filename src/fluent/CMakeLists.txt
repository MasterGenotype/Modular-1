# Fluent HTTP Client Implementation Library

add_library(fluent_client
    Response.cpp
    BodyBuilder.cpp
    Request.cpp
    FluentClient.cpp
    HttpClientBridge.cpp
    RetryCoordinator.cpp
    clients/NexusModsClient.cpp
)

add_library(modular::fluent_client ALIAS fluent_client)

target_include_directories(fluent_client
    PUBLIC
        $<BUILD_INTERFACE:${CMAKE_SOURCE_DIR}/include>
        $<INSTALL_INTERFACE:include>
    PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}
)

target_compile_features(fluent_client PUBLIC cxx_std_20)

# Compiler warnings
target_compile_options(fluent_client PRIVATE
    $<$<CXX_COMPILER_ID:GNU,Clang>:-Wall -Wextra -Wpedantic>
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
)

# Dependencies
find_package(nlohmann_json REQUIRED)
find_package(CURL REQUIRED)

target_link_libraries(fluent_client
    PUBLIC
        modular::fluent_interfaces
        nlohmann_json::nlohmann_json
    PRIVATE
        CURL::libcurl
)

# Install
install(
    TARGETS fluent_client
    EXPORT fluent_client-targets
    LIBRARY DESTINATION lib
    ARCHIVE DESTINATION lib
    RUNTIME DESTINATION bin
)
