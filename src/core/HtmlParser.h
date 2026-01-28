#pragma once

#include <string>
#include <vector>
#include <set>

namespace modular {

/**
 * @brief Minimal HTML parsing utilities for extracting mod IDs from NexusMods HTML
 * 
 * Uses regex-based extraction to be tolerant of HTML structure changes.
 * No external HTML parser dependency required.
 */
class HtmlParser {
public:
    /**
     * @brief Extract unique mod IDs from HTML content
     * 
     * Searches for patterns like "/mods/12345" or "nexusmods.com/{domain}/mods/12345"
     * 
     * @param html HTML content to parse
     * @return Vector of unique mod IDs found
     */
    static std::vector<int> extractModIds(const std::string& html);
    
    /**
     * @brief Check if HTML contains Cloudflare challenge
     * 
     * @param html HTML content
     * @return true if CF challenge detected
     */
    static bool isCloudflareChallenge(const std::string& html);
    
    /**
     * @brief Check if HTML contains login redirect
     * 
     * @param html HTML content
     * @return true if login page detected
     */
    static bool isLoginPage(const std::string& html);
    
    /**
     * @brief Extract text content from HTML element
     * 
     * Simple extraction - gets text between tags
     * 
     * @param html HTML content
     * @param tag_name Tag to extract from (e.g., "title")
     * @return Extracted text, or empty string if not found
     */
    static std::string extractTagContent(const std::string& html, const std::string& tag_name);

private:
    // Helper to remove HTML tags from text
    static std::string stripHtmlTags(const std::string& text);
};

} // namespace modular
