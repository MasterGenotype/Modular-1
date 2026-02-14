using Modular.Core.Backends;
using Modular.Core.Backends.NexusMods;
using Modular.Core.Configuration;
using Modular.Core.Database;
using Modular.Core.RateLimiting;
using Modular.Sdk.Backends;
using Xunit;

namespace Modular.Core.Tests.Backends;

public class NexusModsBackendTests
{
    [Fact]
    public void Id_ReturnsNexusmods()
    {
        var backend = CreateBackend();

        Assert.Equal("nexusmods", backend.Id);
    }

    [Fact]
    public void DisplayName_ReturnsNexusMods()
    {
        var backend = CreateBackend();

        Assert.Equal("NexusMods", backend.DisplayName);
    }

    [Fact]
    public void Capabilities_IncludesExpectedFlags()
    {
        var backend = CreateBackend();

        Assert.True(backend.Capabilities.HasFlag(BackendCapabilities.GameDomains));
        Assert.True(backend.Capabilities.HasFlag(BackendCapabilities.FileCategories));
        Assert.True(backend.Capabilities.HasFlag(BackendCapabilities.Md5Verification));
        Assert.True(backend.Capabilities.HasFlag(BackendCapabilities.RateLimited));
        Assert.True(backend.Capabilities.HasFlag(BackendCapabilities.Authentication));
        Assert.True(backend.Capabilities.HasFlag(BackendCapabilities.ModCategories));
    }

    [Fact]
    public void ValidateConfiguration_ReturnsError_WhenApiKeyMissing()
    {
        var settings = new AppSettings { NexusApiKey = "" };
        var backend = CreateBackend(settings);

        var errors = backend.ValidateConfiguration();

        Assert.Single(errors);
        Assert.Contains("API key", errors[0]);
    }

    [Fact]
    public void ValidateConfiguration_ReturnsEmpty_WhenApiKeyPresent()
    {
        var settings = new AppSettings { NexusApiKey = "test-api-key" };
        var backend = CreateBackend(settings);

        var errors = backend.ValidateConfiguration();

        Assert.Empty(errors);
    }

    [Fact]
    public async Task GetModFilesAsync_ThrowsArgumentException_WhenGameDomainMissing()
    {
        var backend = CreateBackend();

        await Assert.ThrowsAsync<ArgumentException>(
            () => backend.GetModFilesAsync("12345", gameDomain: null));
    }

    [Fact]
    public async Task ResolveDownloadUrlAsync_ThrowsArgumentException_WhenGameDomainMissing()
    {
        var backend = CreateBackend();

        await Assert.ThrowsAsync<ArgumentException>(
            () => backend.ResolveDownloadUrlAsync("12345", "67890", gameDomain: null));
    }

    [Fact]
    public async Task DownloadModsAsync_ThrowsArgumentException_WhenGameDomainMissing()
    {
        var backend = CreateBackend();

        await Assert.ThrowsAsync<ArgumentException>(
            () => backend.DownloadModsAsync("/tmp/mods", gameDomain: null));
    }

    [Fact]
    public async Task GetModInfoAsync_ThrowsArgumentException_WhenGameDomainMissing()
    {
        var backend = CreateBackend();

        await Assert.ThrowsAsync<ArgumentException>(
            () => backend.GetModInfoAsync("12345", gameDomain: null));
    }

    private static NexusModsBackend CreateBackend(AppSettings? settings = null)
    {
        settings ??= new AppSettings { NexusApiKey = "test-key" };
        var rateLimiter = new NexusRateLimiter();
        var database = new DownloadDatabase(":memory:");
        var metadataCache = new ModMetadataCache(":memory:");

        return new NexusModsBackend(settings, rateLimiter, database, metadataCache);
    }
}
