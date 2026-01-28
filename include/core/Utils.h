#pragma once

#include <string>
#include <cstdint>

namespace modular {

/**
 * @brief Sanitizes a filename by replacing invalid characters with underscores
 * @param filename The filename to sanitize
 * @return Sanitized filename safe for filesystem operations
 */
std::string sanitizeFilename(const std::string& filename);

/**
 * @brief URL-encodes spaces in a string (replaces space with %20)
 * @param url The URL string to encode
 * @return URL with spaces escaped
 */
std::string escapeSpaces(const std::string& url);

/**
 * @brief Formats byte count as human-readable string (B, KB, MB, GB, etc.)
 * @param bytes Number of bytes
 * @param precision Decimal precision for formatted output
 * @return Formatted string (e.g., "1.50 MB")
 */
std::string formatBytes(uint64_t bytes, int precision = 2);

/**
 * @brief Calculates MD5 checksum of a file
 * @param filepath Path to the file
 * @return MD5 checksum as hex string (32 characters)
 * @throws FileSystemException if file cannot be read
 */
std::string calculateMD5(const std::string& filepath);

/**
 * @brief Trims whitespace from both ends of a string
 * @param str String to trim
 * @return Trimmed string
 */
std::string trim(const std::string& str);

} // namespace modular
