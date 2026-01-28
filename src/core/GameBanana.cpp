#include "GameBanana.h"
#include "Exceptions.h"
#include "Utils.h"
#include "nlohmann/json.hpp"
#include <filesystem>
#include <fstream>
#include <iostream>
#include <sstream>

using json = nlohmann::json;
namespace fs = std::filesystem;
using modular::HttpClient;
using modular::HttpResponse;

std::string extractModId(const std::string& profileUrl)
{
    const std::string marker = "/mods/";
    size_t pos = profileUrl.find(marker);
    return (pos != std::string::npos) ? profileUrl.substr(pos + marker.length()) : "";
}

std::string extractFileName(const std::string& downloadUrl)
{
    size_t pos = downloadUrl.find_last_of("/");
    return (pos != std::string::npos && pos + 1 < downloadUrl.size()) ? downloadUrl.substr(pos + 1) : "downloaded_file";
}


// Refactored implementations using HttpClient

std::vector<std::pair<std::string, std::string>> fetchSubscribedMods(
    const std::string& userId,
    HttpClient& client)
{
    std::vector<std::pair<std::string, std::string>> mods;
    
    try {
        std::string url = "https://gamebanana.com/apiv11/Member/" + userId + "/Subscriptions";
        HttpResponse response = client.get(url);
        
        json subsJson = json::parse(response.body);
        if (!subsJson.contains("_aRecords")) {
            return mods;
        }
        
        for (const auto& record : subsJson["_aRecords"]) {
            if (record.contains("_aSubscription")) {
                json subscription = record["_aSubscription"];
                if (subscription.contains("_sSingularTitle") && 
                    subscription["_sSingularTitle"] == "Mod" && 
                    subscription.contains("_sProfileUrl") && 
                    subscription.contains("_sName")) {
                    
                    mods.emplace_back(
                        subscription["_sProfileUrl"].get<std::string>(),
                        subscription["_sName"].get<std::string>()
                    );
                }
            }
        }
    } catch (const modular::ParseException& e) {
        std::cerr << "JSON parse error: " << e.what() << std::endl;
    } catch (const modular::ApiException& e) {
        std::cerr << "API error " << e.statusCode() << ": " << e.what() << std::endl;
    } catch (const modular::NetworkException& e) {
        std::cerr << "Network error: " << e.what() << std::endl;
    }
    
    return mods;
}

std::vector<std::string> fetchModFileUrls(
    const std::string& modId,
    HttpClient& client)
{
    std::vector<std::string> urls;
    
    try {
        std::string url = "https://gamebanana.com/apiv11/Mod/" + modId + "?_csvProperties=_aFiles";
        HttpResponse response = client.get(url);
        
        json fileListJson = json::parse(response.body);
        if (!fileListJson.contains("_aFiles")) {
            return urls;
        }
        
        for (const auto& fileEntry : fileListJson["_aFiles"]) {
            if (fileEntry.contains("_sDownloadUrl")) {
                urls.push_back(fileEntry["_sDownloadUrl"].get<std::string>());
            }
        }
    } catch (const modular::ParseException& e) {
        std::cerr << "JSON parse error: " << e.what() << std::endl;
    } catch (const modular::ApiException& e) {
        std::cerr << "API error " << e.statusCode() << ": " << e.what() << std::endl;
    } catch (const modular::NetworkException& e) {
        std::cerr << "Network error: " << e.what() << std::endl;
    }
    
    return urls;
}

void downloadModFiles(
    const std::string& modId,
    const std::string& modName,
    const std::string& baseDir,
    HttpClient& client,
    GameBananaProgressCallback progress_cb)
{
    std::string modFolder = baseDir + "/" + modular::sanitizeFilename(modName);
    fs::create_directories(modFolder);
    
    std::vector<std::string> downloadUrls = fetchModFileUrls(modId, client);
    int fileCount = 0;
    int totalFiles = downloadUrls.size();
    
    for (const auto& url : downloadUrls) {
        std::string filename = std::to_string(++fileCount) + "_" + extractFileName(url);
        std::string outputPath = modFolder + "/" + filename;
        
        if (progress_cb) {
            progress_cb(filename, fileCount - 1, totalFiles);
        }
        
        try {
            client.downloadFile(url, outputPath);
        } catch (const std::exception& e) {
            std::cerr << "Failed to download " << filename << ": " << e.what() << std::endl;
        }
        
        if (progress_cb) {
            progress_cb(filename, fileCount, totalFiles);
        }
    }
}
