using Modular.Core.Backends;
using Modular.Sdk.Backends;
using Modular.Sdk.Backends.Common;
using Xunit;
using IModBackend = Modular.Core.Backends.IModBackend;

namespace Modular.Core.Tests.Backends;

public class BackendRegistryTests
{
    [Fact]
    public void Register_AddsBackendToRegistry()
    {
        var registry = new BackendRegistry();
        var backend = new MockBackend("test", "Test Backend");

        registry.Register(backend);

        Assert.Equal(1, registry.Count);
        Assert.True(registry.IsRegistered("test"));
    }

    [Fact]
    public void Register_ReplacesExistingBackendWithSameId()
    {
        var registry = new BackendRegistry();
        var backend1 = new MockBackend("test", "Test 1");
        var backend2 = new MockBackend("test", "Test 2");

        registry.Register(backend1);
        registry.Register(backend2);

        Assert.Equal(1, registry.Count);
        Assert.Equal("Test 2", registry.Get("test")!.DisplayName);
    }

    [Fact]
    public void Get_ReturnsNullForUnknownBackend()
    {
        var registry = new BackendRegistry();

        var result = registry.Get("unknown");

        Assert.Null(result);
    }

    [Fact]
    public void Get_IsCaseInsensitive()
    {
        var registry = new BackendRegistry();
        registry.Register(new MockBackend("NexusMods", "NexusMods"));

        Assert.NotNull(registry.Get("nexusmods"));
        Assert.NotNull(registry.Get("NEXUSMODS"));
        Assert.NotNull(registry.Get("NexusMods"));
    }

    [Fact]
    public void GetRequired_ThrowsForUnknownBackend()
    {
        var registry = new BackendRegistry();

        Assert.Throws<KeyNotFoundException>(() => registry.GetRequired("unknown"));
    }

    [Fact]
    public void GetAll_ReturnsAllRegisteredBackends()
    {
        var registry = new BackendRegistry();
        registry.Register(new MockBackend("a", "A"));
        registry.Register(new MockBackend("b", "B"));
        registry.Register(new MockBackend("c", "C"));

        var all = registry.GetAll();

        Assert.Equal(3, all.Count);
    }

    [Fact]
    public void GetConfigured_ExcludesBackendsWithValidationErrors()
    {
        var registry = new BackendRegistry();
        registry.Register(new MockBackend("valid", "Valid", isConfigured: true));
        registry.Register(new MockBackend("invalid", "Invalid", isConfigured: false));

        var configured = registry.GetConfigured();

        Assert.Single(configured);
        Assert.Equal("valid", configured[0].Id);
    }

    [Fact]
    public void GetWithCapability_FiltersBackendsByCapability()
    {
        var registry = new BackendRegistry();
        registry.Register(new MockBackend("nexus", "NexusMods",
            capabilities: BackendCapabilities.GameDomains | BackendCapabilities.RateLimited));
        registry.Register(new MockBackend("gb", "GameBanana",
            capabilities: BackendCapabilities.None));

        var withDomains = registry.GetWithCapability(BackendCapabilities.GameDomains);
        var withRateLimiting = registry.GetWithCapability(BackendCapabilities.RateLimited);
        var withNone = registry.GetWithCapability(BackendCapabilities.None);

        Assert.Single(withDomains);
        Assert.Equal("nexus", withDomains[0].Id);
        Assert.Single(withRateLimiting);
        Assert.Equal(2, withNone.Count); // Both have None (as a subset)
    }

    [Fact]
    public void GetWithAllCapabilities_RequiresAllFlags()
    {
        var registry = new BackendRegistry();
        registry.Register(new MockBackend("full", "Full",
            capabilities: BackendCapabilities.GameDomains | BackendCapabilities.RateLimited | BackendCapabilities.Authentication));
        registry.Register(new MockBackend("partial", "Partial",
            capabilities: BackendCapabilities.GameDomains));

        var result = registry.GetWithAllCapabilities(
            BackendCapabilities.GameDomains | BackendCapabilities.RateLimited);

        Assert.Single(result);
        Assert.Equal("full", result[0].Id);
    }

    [Fact]
    public void GetAllConfigurationErrors_ReturnsOnlyBackendsWithErrors()
    {
        var registry = new BackendRegistry();
        registry.Register(new MockBackend("valid", "Valid", isConfigured: true));
        registry.Register(new MockBackend("invalid1", "Invalid 1", isConfigured: false));
        registry.Register(new MockBackend("invalid2", "Invalid 2", isConfigured: false));

        var errors = registry.GetAllConfigurationErrors();

        Assert.Equal(2, errors.Count);
        Assert.False(errors.ContainsKey("valid"));
        Assert.True(errors.ContainsKey("invalid1"));
        Assert.True(errors.ContainsKey("invalid2"));
    }

    [Fact]
    public void Unregister_RemovesBackend()
    {
        var registry = new BackendRegistry();
        registry.Register(new MockBackend("test", "Test"));

        var result = registry.Unregister("test");

        Assert.True(result);
        Assert.Equal(0, registry.Count);
        Assert.False(registry.IsRegistered("test"));
    }

    [Fact]
    public void Unregister_ReturnsFalseForUnknownBackend()
    {
        var registry = new BackendRegistry();

        var result = registry.Unregister("unknown");

        Assert.False(result);
    }

    /// <summary>
    /// Mock backend for testing purposes.
    /// </summary>
    private class MockBackend : IModBackend
    {
        private readonly bool _isConfigured;
        private readonly List<string> _validationErrors;

        public string Id { get; }
        public string DisplayName { get; }
        public BackendCapabilities Capabilities { get; }

        public MockBackend(
            string id,
            string displayName,
            bool isConfigured = true,
            BackendCapabilities capabilities = BackendCapabilities.None)
        {
            Id = id;
            DisplayName = displayName;
            Capabilities = capabilities;
            _isConfigured = isConfigured;
            _validationErrors = isConfigured ? [] : [$"{id} is not configured"];
        }

        public IReadOnlyList<string> ValidateConfiguration() => _validationErrors;

        public Task<List<BackendMod>> GetUserModsAsync(string? gameDomain = null, CancellationToken ct = default)
            => Task.FromResult(new List<BackendMod>());

        public Task<List<BackendModFile>> GetModFilesAsync(string modId, string? gameDomain = null, FileFilter? filter = null, CancellationToken ct = default)
            => Task.FromResult(new List<BackendModFile>());

        public Task<string?> ResolveDownloadUrlAsync(string modId, string fileId, string? gameDomain = null, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task DownloadModsAsync(string outputDirectory, string? gameDomain = null, DownloadOptions? options = null, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<BackendMod?> GetModInfoAsync(string modId, string? gameDomain = null, CancellationToken ct = default)
            => Task.FromResult<BackendMod?>(null);
    }
}
