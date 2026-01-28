#include <fluent/clients/NexusModsClient.h>
#include "../FluentClient.h"
#include "../filters/Filters.h"

namespace modular::fluent {

/// Concrete implementation of NexusModsClient
class NexusModsClientImpl : public NexusModsClient {
public:
    NexusModsClientImpl(
        const std::string& apiKey,
        RateLimiterPtr rateLimiter,
        std::shared_ptr<modular::ILogger> logger
    )
        : apiKey_(apiKey)
        , rateLimiter_(std::move(rateLimiter))
        , logger_(std::move(logger))
    {
        // Create the underlying fluent client
        client_ = std::make_unique<FluentClient>("https://api.nexusmods.com");

        // Configure client
        client_->setUserAgent("Modular/1.0");
        client_->setBearerAuth(apiKey_);  // NexusMods uses apikey header, not Bearer
        
        // Actually NexusMods uses a custom header
        client_->addDefault([this](IRequest& req) {
            req.withHeader("apikey", apiKey_);
            req.withHeader("Accept", "application/json");
        });

        if (rateLimiter_) {
            client_->setRateLimiter(rateLimiter_);
            client_->filters().add(std::make_shared<RateLimitFilter>(rateLimiter_, logger_.get()));
        }

        if (logger_) {
            client_->setLogger(logger_);
            client_->filters().add(std::make_shared<LoggingFilter>(logger_.get()));
        }

        // Add error handling filter
        client_->filters().add(std::make_shared<DefaultErrorFilter>());
    }

    //=========================================================================
    // User Operations
    //=========================================================================

    nlohmann::json validateApiKey() override {
        auto response = client_->getAsync("v1/users/validate.json")->asResponse();
        return response->asJson();
    }

    std::vector<NexusTrackedMod> getTrackedMods() override {
        auto response = client_->getAsync("v1/user/tracked_mods.json")->asResponse();
        auto json = response->asJson();

        std::vector<NexusTrackedMod> mods;
        for (const auto& item : json) {
            NexusTrackedMod mod;
            mod.modId = item.value("mod_id", 0);
            mod.domainName = item.value("domain_name", "");
            // Name might not always be present in tracked_mods response
            if (item.contains("name")) {
                mod.name = item["name"].get<std::string>();
            }
            mods.push_back(mod);
        }
        return mods;
    }

    std::vector<NexusTrackedMod> getTrackedMods(const std::string& gameDomain) override {
        auto allMods = getTrackedMods();
        std::vector<NexusTrackedMod> filtered;
        
        for (const auto& mod : allMods) {
            if (mod.domainName == gameDomain) {
                filtered.push_back(mod);
            }
        }
        return filtered;
    }

    bool isModTracked(const std::string& gameDomain, int modId) override {
        auto mods = getTrackedMods(gameDomain);
        for (const auto& mod : mods) {
            if (mod.modId == modId) {
                return true;
            }
        }
        return false;
    }

    //=========================================================================
    // Mod Information
    //=========================================================================

    nlohmann::json getModInfo(const std::string& gameDomain, int modId) override {
        std::string resource = "v1/games/" + gameDomain + "/mods/" + std::to_string(modId) + ".json";
        auto response = client_->getAsync(resource)->asResponse();
        return response->asJson();
    }

    std::vector<NexusModFile> getModFiles(
        const std::string& gameDomain,
        int modId,
        const std::string& category
    ) override {
        std::string resource = "v1/games/" + gameDomain + "/mods/" + std::to_string(modId) + "/files.json";
        
        auto request = client_->getAsync(resource);
        if (!category.empty()) {
            request->withArgument("category", category);
        }
        
        auto response = request->asResponse();
        auto json = response->asJson();

        std::vector<NexusModFile> files;
        if (json.contains("files")) {
            for (const auto& item : json["files"]) {
                NexusModFile file;
                file.fileId = item.value("file_id", 0);
                file.name = item.value("name", "");
                file.version = item.value("version", "");
                file.categoryName = item.value("category_name", "");
                file.isPrimary = item.value("is_primary", false);
                file.uploadedTimestamp = item.value("uploaded_timestamp", 0);
                file.sizeKb = item.value("size_kb", 0);
                files.push_back(file);
            }
        }
        return files;
    }

    std::optional<NexusModFile> getPrimaryFile(
        const std::string& gameDomain,
        int modId
    ) override {
        auto files = getModFiles(gameDomain, modId, "main");
        
        // First, look for primary file
        for (const auto& file : files) {
            if (file.isPrimary) {
                return file;
            }
        }
        
        // Otherwise, return the most recent file
        if (!files.empty()) {
            auto newest = std::max_element(files.begin(), files.end(),
                [](const NexusModFile& a, const NexusModFile& b) {
                    return a.uploadedTimestamp < b.uploadedTimestamp;
                });
            return *newest;
        }
        
        return std::nullopt;
    }

    //=========================================================================
    // Downloads
    //=========================================================================

    std::vector<NexusDownloadLink> getDownloadLinks(
        const std::string& gameDomain,
        int modId,
        int fileId,
        const std::string& serverKey
    ) override {
        std::string resource = "v1/games/" + gameDomain + "/mods/" + 
                              std::to_string(modId) + "/files/" + 
                              std::to_string(fileId) + "/download_link.json";
        
        auto request = client_->getAsync(resource);
        if (!serverKey.empty()) {
            request->withArgument("key", serverKey);
        }
        
        auto response = request->asResponse();
        auto json = response->asJson();

        std::vector<NexusDownloadLink> links;
        for (const auto& item : json) {
            NexusDownloadLink link;
            link.uri = item.value("URI", "");
            link.name = item.value("name", "");
            link.shortName = item.value("short_name", "");
            links.push_back(link);
        }
        return links;
    }

    void downloadFile(
        const std::string& gameDomain,
        int modId,
        int fileId,
        const std::filesystem::path& outputPath,
        ProgressCallback progress
    ) override {
        // Get download links
        auto links = getDownloadLinks(gameDomain, modId, fileId, "");
        if (links.empty()) {
            throw ApiException(
                "No download links available",
                404, "Not Found", {}, ""
            );
        }

        // Use the first available link
        const auto& link = links[0];

        // Create a separate client for the download (different host)
        FluentClient downloadClient(link.uri);
        downloadClient.setUserAgent("Modular/1.0");
        
        if (logger_) {
            downloadClient.setLogger(logger_);
        }

        // Perform the download
        downloadClient.getAsync("")
            ->withTimeout(std::chrono::seconds{300})  // 5 min timeout for downloads
            .downloadTo(outputPath, progress);
    }

    //=========================================================================
    // Rate Limiting
    //=========================================================================

    RateLimitStatus getRateLimitStatus() const override {
        if (rateLimiter_) {
            return rateLimiter_->status();
        }
        return RateLimitStatus{};
    }

    bool canMakeRequest() const override {
        if (rateLimiter_) {
            return rateLimiter_->canMakeRequest();
        }
        return true;
    }

private:
    std::string apiKey_;
    RateLimiterPtr rateLimiter_;
    std::shared_ptr<modular::ILogger> logger_;
    std::unique_ptr<FluentClient> client_;
};

//=============================================================================
// Factory
//=============================================================================

std::unique_ptr<NexusModsClient> NexusModsClient::create(
    const std::string& apiKey,
    RateLimiterPtr rateLimiter,
    std::shared_ptr<modular::ILogger> logger
) {
    return std::make_unique<NexusModsClientImpl>(apiKey, std::move(rateLimiter), std::move(logger));
}

} // namespace modular::fluent
