#include "BodyBuilder.h"
#include <fstream>
#include <sstream>

namespace modular::fluent {

//=============================================================================
// Form URL Encoded
//=============================================================================

RequestBody BodyBuilder::formUrlEncoded(
    const std::vector<std::pair<std::string, std::string>>& arguments
) {
    std::string body = detail::buildQueryString(arguments);
    return RequestBody(std::move(body), "application/x-www-form-urlencoded");
}

RequestBody BodyBuilder::formUrlEncoded(
    const std::map<std::string, std::string>& arguments
) {
    std::vector<std::pair<std::string, std::string>> pairs;
    pairs.reserve(arguments.size());
    for (const auto& [key, value] : arguments) {
        pairs.emplace_back(key, value);
    }
    return formUrlEncoded(pairs);
}

//=============================================================================
// JSON
//=============================================================================

RequestBody BodyBuilder::jsonBody(const nlohmann::json& json) {
    std::string body = json.dump();
    return RequestBody(std::move(body), "application/json");
}

RequestBody BodyBuilder::rawJson(const std::string& jsonString) {
    return RequestBody(jsonString, "application/json");
}

//=============================================================================
// File Upload
//=============================================================================

RequestBody BodyBuilder::fileUpload(const std::filesystem::path& filePath) {
    std::vector<std::pair<std::string, std::filesystem::path>> files;
    files.emplace_back("file", filePath);
    return fileUpload(files);
}

RequestBody BodyBuilder::fileUpload(
    const std::vector<std::filesystem::path>& filePaths
) {
    std::vector<std::pair<std::string, std::filesystem::path>> files;
    files.reserve(filePaths.size());
    int index = 0;
    for (const auto& path : filePaths) {
        files.emplace_back("file" + std::to_string(index++), path);
    }
    return fileUpload(files);
}

RequestBody BodyBuilder::fileUpload(
    const std::vector<std::pair<std::string, std::filesystem::path>>& files
) {
    std::vector<MultipartPart> parts;
    parts.reserve(files.size());

    for (const auto& [fieldName, filePath] : files) {
        // Read file content
        std::ifstream file(filePath, std::ios::binary);
        if (!file) {
            throw std::runtime_error("Failed to open file: " + filePath.string());
        }

        std::vector<uint8_t> data(
            (std::istreambuf_iterator<char>(file)),
            std::istreambuf_iterator<char>()
        );

        MultipartPart part;
        part.name = fieldName;
        part.filename = filePath.filename().string();
        part.contentType = detail::getMimeType(filePath);
        part.data = std::move(data);

        parts.push_back(std::move(part));
    }

    return buildMultipart(parts);
}

RequestBody BodyBuilder::fileUpload(
    const std::string& fieldName,
    const std::string& fileName,
    const std::vector<uint8_t>& data,
    const std::string& mimeType
) {
    std::vector<MultipartPart> parts;

    MultipartPart part;
    part.name = fieldName;
    part.filename = fileName;
    part.contentType = mimeType;
    part.data = data;

    parts.push_back(std::move(part));

    return buildMultipart(parts);
}

//=============================================================================
// Raw Content
//=============================================================================

RequestBody BodyBuilder::raw(
    const std::string& content,
    const std::string& contentType
) {
    return RequestBody(content, contentType);
}

RequestBody BodyBuilder::raw(
    const std::vector<uint8_t>& content,
    const std::string& contentType
) {
    return RequestBody(content, contentType);
}

//=============================================================================
// Multipart Helper
//=============================================================================

RequestBody BodyBuilder::buildMultipart(const std::vector<MultipartPart>& parts) {
    std::string boundary = detail::generateBoundary();
    std::vector<uint8_t> body;

    for (const auto& part : parts) {
        // Boundary line
        std::string boundaryLine = "--" + boundary + "\r\n";
        body.insert(body.end(), boundaryLine.begin(), boundaryLine.end());

        // Content-Disposition header
        std::string disposition = "Content-Disposition: form-data; name=\"" +
                                  part.name + "\"";
        if (!part.filename.empty()) {
            disposition += "; filename=\"" + part.filename + "\"";
        }
        disposition += "\r\n";
        body.insert(body.end(), disposition.begin(), disposition.end());

        // Content-Type header
        std::string contentType = "Content-Type: " + part.contentType + "\r\n\r\n";
        body.insert(body.end(), contentType.begin(), contentType.end());

        // Content
        body.insert(body.end(), part.data.begin(), part.data.end());

        // Trailing CRLF
        std::string crlf = "\r\n";
        body.insert(body.end(), crlf.begin(), crlf.end());
    }

    // Final boundary
    std::string finalBoundary = "--" + boundary + "--\r\n";
    body.insert(body.end(), finalBoundary.begin(), finalBoundary.end());

    std::string contentType = "multipart/form-data; boundary=" + boundary;
    return RequestBody(std::move(body), std::move(contentType));
}

} // namespace modular::fluent
