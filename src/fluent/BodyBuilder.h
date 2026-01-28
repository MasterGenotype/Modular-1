#pragma once

#include <fluent/IBodyBuilder.h>
#include "Utils.h"

namespace modular::fluent {

/// Concrete implementation of IBodyBuilder
class BodyBuilder : public IBodyBuilder {
public:
    BodyBuilder() = default;
    ~BodyBuilder() override = default;

    //=========================================================================
    // Form URL Encoded
    //=========================================================================

    RequestBody formUrlEncoded(
        const std::vector<std::pair<std::string, std::string>>& arguments
    ) override;

    RequestBody formUrlEncoded(
        const std::map<std::string, std::string>& arguments
    ) override;

    //=========================================================================
    // JSON
    //=========================================================================

    RequestBody jsonBody(const nlohmann::json& json) override;
    RequestBody rawJson(const std::string& jsonString) override;

    //=========================================================================
    // File Upload
    //=========================================================================

    RequestBody fileUpload(const std::filesystem::path& filePath) override;
    
    RequestBody fileUpload(
        const std::vector<std::filesystem::path>& filePaths
    ) override;
    
    RequestBody fileUpload(
        const std::vector<std::pair<std::string, std::filesystem::path>>& files
    ) override;
    
    RequestBody fileUpload(
        const std::string& fieldName,
        const std::string& fileName,
        const std::vector<uint8_t>& data,
        const std::string& mimeType = "application/octet-stream"
    ) override;

    //=========================================================================
    // Raw Content
    //=========================================================================

    RequestBody raw(
        const std::string& content,
        const std::string& contentType = "text/plain"
    ) override;

    RequestBody raw(
        const std::vector<uint8_t>& content,
        const std::string& contentType = "application/octet-stream"
    ) override;

private:
    /// Build multipart form data from parts
    struct MultipartPart {
        std::string name;
        std::string filename;
        std::string contentType;
        std::vector<uint8_t> data;
    };

    RequestBody buildMultipart(const std::vector<MultipartPart>& parts);
};

} // namespace modular::fluent
