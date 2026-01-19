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

# Create executable
add_executable(${PROJECT_NAME}
    src/main.cpp
    src/GameBanana.cpp
    src/NexusMods.cpp
    src/Rename.cpp
    include/GameBanana.h
    include/NexusMods.h
    include/Rename.h
)

# Link libraries
target_link_libraries(${PROJECT_NAME} 
    PRIVATE 
    CURL::libcurl
    nlohmann_json::nlohmann_json
)

target_include_directories(${PROJECT_NAME} PRIVATE 
    src/
    ${CMAKE_SOURCE_DIR}     # Project root for headers
    ${CMAKE_SOURCE_DIR}/include  # If you have include/ folder
)

# Compiler warnings
if (MSVC)
    target_compile_options(${PROJECT_NAME} PRIVATE /W4 /WX)
else()
    target_compile_options(${PROJECT_NAME} PRIVATE -Wall -Wextra -Wpedantic -Werror)
endif()

# Install target
install(TARGETS ${PROJECT_NAME} DESTINATION bin)
