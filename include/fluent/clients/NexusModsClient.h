#pragma once

#include <fluent/Fluent.h>
#include <core/ILogger.h>

#include <string>
#include <vector>
#include <optional>

namespace modular::fluent {

/// Tracked mod information from NexusMods
struct NexusTrackedMod {
    int modId = 0;
    std::string domainName;
    std::string name;
};

/// Mod file information
struct NexusModFile {
    int fileId = 0;
    std::string name;
    std::string version;
    std::string categoryName;
    bool isPrimary = false;
    int64_t uploadedTimestamp = 0;
    int64_t sizeKb = 0;
};

/// Download link information
struct NexusDownloadLink {
    std::string uri;
    std::string name;
    std::string shortName;
};

/// High-level fluent client for NexusMods API.
/// Provides type-safe methods for common NexusMods operations.
///
/// Example:
/// @code
/// auto nexus = NexusModsClient::create(apiKey);
/// auto mods = nexus->getTrackedMods();
/// for (const auto& mod : mods) {
///     std::cout << mod.name << " (" << mod.domainName << ")\n";
/// }
/// @endcode
class NexusModsClient {
public:
    /// Create a new NexusMods client
    /// @param apiKey Your NexusMods API key
    /// @param rateLimiter Optional rate limiter for API compliance
    /// @param logger Optional logger for debugging
    static std::unique_ptr<NexusModsClient> create(
        const std::string& apiKey,
        RateLimiterPtr rateLimiter = nullptr,
        std::shared_ptr<modular::ILogger> logger = nullptr
    );

    virtual ~NexusModsClient() = default;

    //=========================================================================
    // User Operations
    //=========================================================================

    /// Validate the API key and get user information
    /// @return JSON with user info, or throws on invalid key
    virtual nlohmann::json validateApiKey() = 0;

    /// Get all tracked mods for the authenticated user
    virtual std::vector<NexusTrackedMod> getTrackedMods() = 0;

    /// Get tracked mods filtered by game domain
    virtual std::vector<NexusTrackedMod> getTrackedMods(const std::string& gameDomain) = 0;

    /// Check if a specific mod is tracked
    virtual bool isModTracked(const std::string& gameDomain, int modId) = 0;

    //=========================================================================
    // Mod Information
    //=========================================================================

    /// Get mod information
    virtual nlohmann::json getModInfo(const std::string& gameDomain, int modId) = 0;

    /// Get files for a mod
    virtual std::vector<NexusModFile> getModFiles(
        const std::string& gameDomain,
        int modId,
        const std::string& category = ""
    ) = 0;

    /// Get the primary/latest file for a mod
    virtual std::optional<NexusModFile> getPrimaryFile(
        const std::string& gameDomain,
        int modId
    ) = 0;

    //=========================================================================
    // Downloads
    //=========================================================================

    /// Generate download links for a file
    /// @param gameDomain Game domain (e.g., "stardewvalley")
    /// @param modId Mod ID
    /// @param fileId File ID
    /// @param serverKey Optional: preferred server key
    virtual std::vector<NexusDownloadLink> getDownloadLinks(
        const std::string& gameDomain,
        int modId,
        int fileId,
        const std::string& serverKey = ""
    ) = 0;

    /// Download a file to disk
    /// @param gameDomain Game domain
    /// @param modId Mod ID
    /// @param fileId File ID
    /// @param outputPath Where to save the file
    /// @param progress Optional progress callback
    virtual void downloadFile(
        const std::string& gameDomain,
        int modId,
        int fileId,
        const std::filesystem::path& outputPath,
        ProgressCallback progress = nullptr
    ) = 0;

    //=========================================================================
    // Rate Limiting
    //=========================================================================

    /// Get current rate limit status
    virtual RateLimitStatus getRateLimitStatus() const = 0;

    /// Check if we can make a request without hitting limits
    virtual bool canMakeRequest() const = 0;
};

} // namespace modular::fluent
