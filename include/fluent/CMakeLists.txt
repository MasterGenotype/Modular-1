# Fluent HTTP Client Interface Library
# This is a header-only interface library

add_library(fluent_interfaces INTERFACE)
add_library(modular::fluent_interfaces ALIAS fluent_interfaces)

target_include_directories(fluent_interfaces
    INTERFACE
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/..>
        $<INSTALL_INTERFACE:include>
)

target_compile_features(fluent_interfaces INTERFACE cxx_std_17)

# Dependencies
find_package(nlohmann_json REQUIRED)
target_link_libraries(fluent_interfaces INTERFACE nlohmann_json::nlohmann_json)

# Install headers
install(
    DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}/
    DESTINATION include/fluent
    FILES_MATCHING PATTERN "*.h"
)

install(
    TARGETS fluent_interfaces
    EXPORT fluent_interfaces-targets
)
