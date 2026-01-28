#include "HtmlParser.h"
#include <regex>
#include <algorithm>

namespace modular {

std::vector<int> HtmlParser::extractModIds(const std::string& html) {
    std::set<int> unique_ids;
    
    // Pattern: /mods/{digits} - captures the mod ID
    // Matches: /mods/12345, /stardewvalley/mods/12345, etc.
    std::regex mod_pattern(R"(/mods/(\d+))");
    
    auto words_begin = std::sregex_iterator(html.begin(), html.end(), mod_pattern);
    auto words_end = std::sregex_iterator();
    
    for (std::sregex_iterator i = words_begin; i != words_end; ++i) {
        std::smatch match = *i;
        if (match.size() > 1) {
            try {
                int mod_id = std::stoi(match[1].str());
                unique_ids.insert(mod_id);
            } catch (const std::exception&) {
                // Skip invalid numbers
            }
        }
    }
    
    // Convert set to vector
    return std::vector<int>(unique_ids.begin(), unique_ids.end());
}

bool HtmlParser::isCloudflareChallenge(const std::string& html) {
    // Common Cloudflare challenge indicators
    std::vector<std::string> cf_markers = {
        "Attention Required",
        "captcha",
        "cf-browser-verification",
        "Checking your browser",
        "__cf_chl_jschl_tk__"
    };
    
    for (const auto& marker : cf_markers) {
        if (html.find(marker) != std::string::npos) {
            return true;
        }
    }
    
    return false;
}

bool HtmlParser::isLoginPage(const std::string& html) {
    // Login page indicators
    std::vector<std::string> login_markers = {
        "<form",
        "login",
        "sign in",
        "username",
        "password"
    };
    
    int marker_count = 0;
    for (const auto& marker : login_markers) {
        std::string lowercase_marker = marker;
        std::string lowercase_html = html;
        
        std::transform(lowercase_marker.begin(), lowercase_marker.end(), 
                      lowercase_marker.begin(), ::tolower);
        std::transform(lowercase_html.begin(), lowercase_html.end(), 
                      lowercase_html.begin(), ::tolower);
        
        if (lowercase_html.find(lowercase_marker) != std::string::npos) {
            marker_count++;
        }
    }
    
    // Need at least 3 login indicators to be confident
    return marker_count >= 3;
}

std::string HtmlParser::extractTagContent(const std::string& html, const std::string& tag_name) {
    // Simple tag extraction: <tag>content</tag>
    std::string open_tag = "<" + tag_name;
    std::string close_tag = "</" + tag_name + ">";
    
    size_t start = html.find(open_tag);
    if (start == std::string::npos) {
        return "";
    }
    
    // Find the end of the opening tag
    size_t content_start = html.find('>', start);
    if (content_start == std::string::npos) {
        return "";
    }
    content_start++; // Move past the '>'
    
    size_t content_end = html.find(close_tag, content_start);
    if (content_end == std::string::npos) {
        return "";
    }
    
    std::string content = html.substr(content_start, content_end - content_start);
    return stripHtmlTags(content);
}

std::string HtmlParser::stripHtmlTags(const std::string& text) {
    std::regex tag_pattern("<[^>]*>");
    return std::regex_replace(text, tag_pattern, "");
}

} // namespace modular
