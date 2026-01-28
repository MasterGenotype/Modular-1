#pragma once

#include "Types.h"
#include <nlohmann/json.hpp>
#include <filesystem>
#include <sstream>

namespace modular::fluent {

/// Represents an HTTP request body that can be sent
struct RequestBody {
    /// The body content as bytes
    std::vector<uint8_t> content;

    /// The Content-Type header value
    std::string contentType;

    /// Create empty body
    RequestBody() = default;

    /// Create body from string
    RequestBody(std::string data, std::string type)
        : content(data.begin(), data.end())
        , contentType(std::move(type))
    {}

    /// Create body from bytes
    RequestBody(std::vector<uint8_t> data, std::string type)
        : content(std::move(data))
        , contentType(std::move(type))
    {}

    bool empty() const { return content.empty(); }
    size_t size() const { return content.size(); }
};

/// Interface for constructing HTTP request bodies.
/// Mirrors FluentHttpClient's IBodyBuilder interface.
class IBodyBuilder {
public:
    virtual ~IBodyBuilder() = default;

    //=========================================================================
    // Form URL Encoded
    //=========================================================================

    /// Create a form URL-encoded body from key-value pairs
    /// Content-Type: application/x-www-form-urlencoded
    virtual RequestBody formUrlEncoded(
        const std::vector<std::pair<std::string, std::string>>& arguments
    ) = 0;

    /// Create a form URL-encoded body from a map
    virtual RequestBody formUrlEncoded(
        const std::map<std::string, std::string>& arguments
    ) = 0;

    //=========================================================================
    // JSON Model
    //=========================================================================

    /// Create a JSON body from a serializable object
    /// Content-Type: application/json
    template<typename T>
    RequestBody model(const T& value) {
        nlohmann::json j = value;
        return jsonBody(j);
    }

    /// Create a JSON body from a json object
    virtual RequestBody jsonBody(const nlohmann::json& json) = 0;

    /// Create a JSON body from a raw string (no validation)
    virtual RequestBody rawJson(const std::string& jsonString) = 0;

    //=========================================================================
    // File Upload (Multipart Form Data)
    //=========================================================================

    /// Create a multipart form-data body for file upload
    /// Content-Type: multipart/form-data; boundary=...
    virtual RequestBody fileUpload(const std::filesystem::path& filePath) = 0;

    /// Create a multipart form-data body for multiple files
    virtual RequestBody fileUpload(
        const std::vector<std::filesystem::path>& filePaths
    ) = 0;

    /// Create a multipart form-data body with custom field names
    virtual RequestBody fileUpload(
        const std::vector<std::pair<std::string, std::filesystem::path>>& files
    ) = 0;

    /// Create a multipart form-data body from in-memory data
    virtual RequestBody fileUpload(
        const std::string& fieldName,
        const std::string& fileName,
        const std::vector<uint8_t>& data,
        const std::string& mimeType = "application/octet-stream"
    ) = 0;

    //=========================================================================
    // Raw Content
    //=========================================================================

    /// Create a body with raw string content
    virtual RequestBody raw(
        const std::string& content,
        const std::string& contentType = "text/plain"
    ) = 0;

    /// Create a body with raw binary content
    virtual RequestBody raw(
        const std::vector<uint8_t>& content,
        const std::string& contentType = "application/octet-stream"
    ) = 0;
};

/// Unique pointer type for body builder
using BodyBuilderPtr = std::unique_ptr<IBodyBuilder>;

} // namespace modular::fluent
