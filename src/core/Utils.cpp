#include "Utils.h"
#include "Exceptions.h"
#include <algorithm>
#include <cctype>
#include <fstream>
#include <iomanip>
#include <sstream>
#include <openssl/evp.h>

namespace modular {

std::string sanitizeFilename(const std::string& filename)
{
    std::string sanitized = filename;
    for (char& c : sanitized) {
        // Replace filesystem-unsafe characters with underscores
        if (c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' ||
            c == '"' || c == '<' || c == '>' || c == '|') {
            c = '_';
        }
    }
    return sanitized;
}

std::string escapeSpaces(const std::string& url)
{
    std::string result;
    result.reserve(url.size());
    for (char c : url) {
        if (c == ' ') {
            result += "%20";
        } else {
            result += c;
        }
    }
    return result;
}

std::string formatBytes(uint64_t bytes, int precision)
{
    const char* units[] = {"B", "KB", "MB", "GB", "TB", "PB"};
    const int num_units = sizeof(units) / sizeof(units[0]);
    
    double size = static_cast<double>(bytes);
    int unit_index = 0;
    
    while (size >= 1024.0 && unit_index < num_units - 1) {
        size /= 1024.0;
        ++unit_index;
    }
    
    std::ostringstream oss;
    oss << std::fixed << std::setprecision(precision) << size << " " << units[unit_index];
    return oss.str();
}

std::string calculateMD5(const std::string& filepath)
{
    std::ifstream file(filepath, std::ios::binary);
    if (!file) {
        throw FileSystemException("Failed to open file for MD5 calculation: " + filepath);
    }
    
    // Use modern EVP API instead of deprecated MD5_* functions
    EVP_MD_CTX* context = EVP_MD_CTX_new();
    if (!context) {
        throw FileSystemException("Failed to create MD5 context", filepath);
    }
    
    if (EVP_DigestInit_ex(context, EVP_md5(), nullptr) != 1) {
        EVP_MD_CTX_free(context);
        throw FileSystemException("Failed to initialize MD5 digest", filepath);
    }
    
    constexpr size_t BUFFER_SIZE = 8192;
    char buffer[BUFFER_SIZE];
    
    while (file.read(buffer, BUFFER_SIZE) || file.gcount() > 0) {
        if (EVP_DigestUpdate(context, buffer, file.gcount()) != 1) {
            EVP_MD_CTX_free(context);
            throw FileSystemException("Failed to update MD5 digest", filepath);
        }
    }
    
    if (file.bad()) {
        EVP_MD_CTX_free(context);
        throw FileSystemException("Error reading file for MD5 calculation: " + filepath);
    }
    
    unsigned char digest[EVP_MAX_MD_SIZE];
    unsigned int digest_len = 0;
    
    if (EVP_DigestFinal_ex(context, digest, &digest_len) != 1) {
        EVP_MD_CTX_free(context);
        throw FileSystemException("Failed to finalize MD5 digest", filepath);
    }
    
    EVP_MD_CTX_free(context);
    
    // Convert digest to hex string
    std::ostringstream oss;
    for (unsigned int i = 0; i < digest_len; ++i) {
        oss << std::hex << std::setw(2) << std::setfill('0') 
            << static_cast<int>(digest[i]);
    }
    
    return oss.str();
}

std::string trim(const std::string& str)
{
    auto start = std::find_if_not(str.begin(), str.end(), 
        [](unsigned char ch) { return std::isspace(ch); });
    auto end = std::find_if_not(str.rbegin(), str.rend(),
        [](unsigned char ch) { return std::isspace(ch); }).base();
    
    return (start < end) ? std::string(start, end) : std::string();
}

} // namespace modular
