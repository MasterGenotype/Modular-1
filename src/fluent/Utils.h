#pragma once

#include <string>
#include <string_view>
#include <vector>
#include <map>
#include <algorithm>
#include <cctype>
#include <sstream>
#include <iomanip>
#include <random>
#include <filesystem>

namespace modular::fluent::detail {

//=============================================================================
// URL Encoding
//=============================================================================

/// URL-encode a string (RFC 3986)
inline std::string urlEncode(std::string_view input) {
    std::ostringstream encoded;
    encoded.fill('0');
    encoded << std::hex;

    for (char c : input) {
        // Keep alphanumeric and other accepted characters intact
        if (std::isalnum(static_cast<unsigned char>(c)) ||
            c == '-' || c == '_' || c == '.' || c == '~') {
            encoded << c;
        } else {
            // Percent-encode all other characters
            encoded << std::uppercase;
            encoded << '%' << std::setw(2) << int(static_cast<unsigned char>(c));
            encoded << std::nouppercase;
        }
    }

    return encoded.str();
}

/// URL-decode a string
inline std::string urlDecode(std::string_view input) {
    std::string decoded;
    decoded.reserve(input.size());

    for (size_t i = 0; i < input.size(); ++i) {
        if (input[i] == '%' && i + 2 < input.size()) {
            int value;
            std::istringstream iss(std::string(input.substr(i + 1, 2)));
            if (iss >> std::hex >> value) {
                decoded += static_cast<char>(value);
                i += 2;
            } else {
                decoded += input[i];
            }
        } else if (input[i] == '+') {
            decoded += ' ';
        } else {
            decoded += input[i];
        }
    }

    return decoded;
}

/// Build a query string from key-value pairs
inline std::string buildQueryString(
    const std::vector<std::pair<std::string, std::string>>& params
) {
    if (params.empty()) return "";

    std::ostringstream oss;
    bool first = true;
    for (const auto& [key, value] : params) {
        if (!first) oss << '&';
        first = false;
        oss << urlEncode(key) << '=' << urlEncode(value);
    }
    return oss.str();
}

//=============================================================================
// Base64 Encoding (for Basic Auth)
//=============================================================================

inline constexpr char base64Chars[] =
    "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

inline std::string base64Encode(std::string_view input) {
    std::string encoded;
    encoded.reserve(((input.size() + 2) / 3) * 4);

    int val = 0;
    int bits = -6;

    for (unsigned char c : input) {
        val = (val << 8) + c;
        bits += 8;
        while (bits >= 0) {
            encoded.push_back(base64Chars[(val >> bits) & 0x3F]);
            bits -= 6;
        }
    }

    if (bits > -6) {
        encoded.push_back(base64Chars[((val << 8) >> (bits + 8)) & 0x3F]);
    }

    while (encoded.size() % 4) {
        encoded.push_back('=');
    }

    return encoded;
}

//=============================================================================
// String Utilities
//=============================================================================

/// Case-insensitive string comparison
inline bool iequals(std::string_view a, std::string_view b) {
    if (a.size() != b.size()) return false;
    return std::equal(a.begin(), a.end(), b.begin(),
        [](char ca, char cb) {
            return std::tolower(static_cast<unsigned char>(ca)) ==
                   std::tolower(static_cast<unsigned char>(cb));
        });
}

/// Trim whitespace from both ends
inline std::string trim(std::string_view str) {
    auto start = str.find_first_not_of(" \t\r\n");
    if (start == std::string_view::npos) return "";
    auto end = str.find_last_not_of(" \t\r\n");
    return std::string(str.substr(start, end - start + 1));
}

/// Split string by delimiter
inline std::vector<std::string> split(std::string_view str, char delimiter) {
    std::vector<std::string> result;
    size_t start = 0;
    size_t end;

    while ((end = str.find(delimiter, start)) != std::string_view::npos) {
        result.emplace_back(str.substr(start, end - start));
        start = end + 1;
    }
    result.emplace_back(str.substr(start));

    return result;
}

/// Join strings with delimiter
template<typename Container>
std::string join(const Container& items, std::string_view delimiter) {
    std::ostringstream oss;
    bool first = true;
    for (const auto& item : items) {
        if (!first) oss << delimiter;
        first = false;
        oss << item;
    }
    return oss.str();
}

//=============================================================================
// Multipart Boundary Generation
//=============================================================================

inline std::string generateBoundary() {
    static constexpr char chars[] =
        "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    std::random_device rd;
    std::mt19937 gen(rd());
    std::uniform_int_distribution<> dis(0, sizeof(chars) - 2);

    std::string boundary = "----ModularBoundary";
    for (int i = 0; i < 16; ++i) {
        boundary += chars[dis(gen)];
    }
    return boundary;
}

//=============================================================================
// MIME Type Detection
//=============================================================================

inline std::string getMimeType(const std::filesystem::path& path) {
    static const std::map<std::string, std::string> mimeTypes = {
        {".json", "application/json"},
        {".xml", "application/xml"},
        {".zip", "application/zip"},
        {".7z", "application/x-7z-compressed"},
        {".rar", "application/vnd.rar"},
        {".txt", "text/plain"},
        {".html", "text/html"},
        {".css", "text/css"},
        {".js", "application/javascript"},
        {".png", "image/png"},
        {".jpg", "image/jpeg"},
        {".jpeg", "image/jpeg"},
        {".gif", "image/gif"},
        {".webp", "image/webp"},
        {".pdf", "application/pdf"},
    };

    auto ext = path.extension().string();
    std::transform(ext.begin(), ext.end(), ext.begin(),
        [](unsigned char c) { return std::tolower(c); });

    auto it = mimeTypes.find(ext);
    return it != mimeTypes.end() ? it->second : "application/octet-stream";
}

//=============================================================================
// Header Parsing
//=============================================================================

/// Parse a header value with parameters (e.g., "text/html; charset=utf-8")
struct HeaderValue {
    std::string value;
    std::map<std::string, std::string> params;
};

inline HeaderValue parseHeaderValue(std::string_view header) {
    HeaderValue result;
    auto parts = split(header, ';');

    if (!parts.empty()) {
        result.value = trim(parts[0]);

        for (size_t i = 1; i < parts.size(); ++i) {
            auto param = trim(parts[i]);
            auto eq = param.find('=');
            if (eq != std::string::npos) {
                auto key = trim(param.substr(0, eq));
                auto val = trim(param.substr(eq + 1));
                // Remove quotes if present
                if (val.size() >= 2 && val.front() == '"' && val.back() == '"') {
                    val = val.substr(1, val.size() - 2);
                }
                result.params[std::string(key)] = std::string(val);
            }
        }
    }

    return result;
}

/// Find header value case-insensitively
inline std::string findHeader(
    const std::map<std::string, std::string>& headers,
    std::string_view name
) {
    for (const auto& [key, value] : headers) {
        if (iequals(key, name)) {
            return value;
        }
    }
    return "";
}

/// HTTP status reason phrases
inline std::string getStatusReason(int code) {
    static const std::map<int, std::string> reasons = {
        {200, "OK"}, {201, "Created"}, {202, "Accepted"},
        {204, "No Content"}, {206, "Partial Content"},
        {301, "Moved Permanently"}, {302, "Found"}, {304, "Not Modified"},
        {400, "Bad Request"}, {401, "Unauthorized"}, {403, "Forbidden"},
        {404, "Not Found"}, {405, "Method Not Allowed"},
        {408, "Request Timeout"}, {409, "Conflict"},
        {429, "Too Many Requests"},
        {500, "Internal Server Error"}, {502, "Bad Gateway"},
        {503, "Service Unavailable"}, {504, "Gateway Timeout"}
    };

    auto it = reasons.find(code);
    return it != reasons.end() ? it->second : "Unknown";
}

} // namespace modular::fluent::detail
