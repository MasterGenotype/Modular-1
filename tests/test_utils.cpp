#include <catch2/catch_test_macros.hpp>
#include "Utils.h"
#include "Exceptions.h"
#include <filesystem>
#include <fstream>

using namespace modular;

TEST_CASE("sanitizeFilename removes invalid characters", "[utils]") {
    SECTION("replaces slashes") {
        REQUIRE(sanitizeFilename("file/name") == "file_name");
        REQUIRE(sanitizeFilename("file\\name") == "file_name");
    }
    
    SECTION("replaces special characters") {
        REQUIRE(sanitizeFilename("file:name") == "file_name");
        REQUIRE(sanitizeFilename("file*name") == "file_name");
        REQUIRE(sanitizeFilename("file?name") == "file_name");
        REQUIRE(sanitizeFilename("file\"name") == "file_name");
        REQUIRE(sanitizeFilename("file<name>") == "file_name_");
        REQUIRE(sanitizeFilename("file|name") == "file_name");
    }
    
    SECTION("preserves valid characters") {
        REQUIRE(sanitizeFilename("valid-file_name.txt") == "valid-file_name.txt");
        REQUIRE(sanitizeFilename("MyMod v1.2.3") == "MyMod v1.2.3");
    }
    
    SECTION("handles empty string") {
        REQUIRE(sanitizeFilename("") == "");
    }
}

TEST_CASE("escapeSpaces URL-encodes spaces", "[utils]") {
    SECTION("replaces single space") {
        REQUIRE(escapeSpaces("hello world") == "hello%20world");
    }
    
    SECTION("replaces multiple spaces") {
        REQUIRE(escapeSpaces("a b c d") == "a%20b%20c%20d");
    }
    
    SECTION("preserves non-space characters") {
        REQUIRE(escapeSpaces("no-spaces-here") == "no-spaces-here");
    }
    
    SECTION("handles empty string") {
        REQUIRE(escapeSpaces("") == "");
    }
    
    SECTION("handles URL with spaces") {
        REQUIRE(escapeSpaces("http://example.com/my file.zip") == 
                "http://example.com/my%20file.zip");
    }
}

TEST_CASE("formatBytes converts bytes to human-readable format", "[utils]") {
    SECTION("bytes") {
        REQUIRE(formatBytes(512) == "512.00 B");
        REQUIRE(formatBytes(1023) == "1023.00 B");
    }
    
    SECTION("kilobytes") {
        REQUIRE(formatBytes(1024) == "1.00 KB");
        REQUIRE(formatBytes(1536) == "1.50 KB");
    }
    
    SECTION("megabytes") {
        REQUIRE(formatBytes(1024 * 1024) == "1.00 MB");
        REQUIRE(formatBytes(1024 * 1024 * 2.5) == "2.50 MB");
    }
    
    SECTION("gigabytes") {
        REQUIRE(formatBytes(1024ULL * 1024 * 1024) == "1.00 GB");
    }
    
    SECTION("zero bytes") {
        REQUIRE(formatBytes(0) == "0.00 B");
    }
}

TEST_CASE("trim removes whitespace from both ends", "[utils]") {
    SECTION("trims leading whitespace") {
        REQUIRE(trim("  hello") == "hello");
        REQUIRE(trim("\thello") == "hello");
        REQUIRE(trim("\n\nhello") == "hello");
    }
    
    SECTION("trims trailing whitespace") {
        REQUIRE(trim("hello  ") == "hello");
        REQUIRE(trim("hello\t") == "hello");
        REQUIRE(trim("hello\n\n") == "hello");
    }
    
    SECTION("trims both ends") {
        REQUIRE(trim("  hello  ") == "hello");
        REQUIRE(trim("\t\nhello\n\t") == "hello");
    }
    
    SECTION("preserves internal whitespace") {
        REQUIRE(trim("  hello world  ") == "hello world");
    }
    
    SECTION("handles empty string") {
        REQUIRE(trim("") == "");
    }
    
    SECTION("handles all whitespace") {
        REQUIRE(trim("   \t\n   ") == "");
    }
}

TEST_CASE("calculateMD5 computes correct hash", "[utils]") {
    namespace fs = std::filesystem;
    
    SECTION("calculates MD5 for small file") {
        // Create temporary test file
        std::string test_path = "/tmp/modular_test_md5.txt";
        {
            std::ofstream f(test_path);
            f << "Hello, World!";
        }
        
        // MD5 of "Hello, World!" is 65a8e27d8879283831b664bd8b7f0ad4
        std::string md5 = calculateMD5(test_path);
        REQUIRE(md5 == "65a8e27d8879283831b664bd8b7f0ad4");
        
        // Cleanup
        fs::remove(test_path);
    }
    
    SECTION("calculates MD5 for empty file") {
        std::string test_path = "/tmp/modular_test_empty.txt";
        {
            std::ofstream f(test_path);
            // Empty file
        }
        
        // MD5 of empty file is d41d8cd98f00b204e9800998ecf8427e
        std::string md5 = calculateMD5(test_path);
        REQUIRE(md5 == "d41d8cd98f00b204e9800998ecf8427e");
        
        // Cleanup
        fs::remove(test_path);
    }
    
    SECTION("throws on non-existent file") {
        REQUIRE_THROWS_AS(calculateMD5("/nonexistent/file.txt"), FileSystemException);
    }
}
