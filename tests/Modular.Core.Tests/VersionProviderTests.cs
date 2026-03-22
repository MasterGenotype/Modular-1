using FluentAssertions;
using Modular.Core.Backends.GameBanana;
using Modular.Core.Backends.NexusMods;
using Modular.Core.Dependencies;
using Modular.Core.Metadata;
using Modular.Core.Versioning;
using Moq;
using Modular.Core.Backends;
using Modular.Sdk.Backends.Common;
using Xunit;

namespace Modular.Core.Tests;

public class NexusModsVersionProviderTests
{
    [Theory]
    [InlineData("nexusmods:skyrimspecialedition:1234", "skyrimspecialedition", "1234")]
    [InlineData("nexusmods:5678", "stardewvalley", "5678")]
    [InlineData("42", "stardewvalley", "42")]
    public void ParseCanonicalId_ValidFormats(string canonicalId, string expectedDomain, string expectedModId)
    {
        var backend = new Mock<IModBackend>().Object;
        var provider = new NexusModsVersionProvider(backend, "stardewvalley");

        var (domain, modId) = provider.ParseCanonicalId(canonicalId);

        domain.Should().Be(expectedDomain);
        modId.Should().Be(expectedModId);
    }

    [Theory]
    [InlineData("nexusmods:a:b:c")]
    [InlineData("other:extra:fields:here")]
    public void ParseCanonicalId_InvalidFormat_Throws(string canonicalId)
    {
        var backend = new Mock<IModBackend>().Object;
        var provider = new NexusModsVersionProvider(backend, "stardewvalley");

        var act = () => provider.ParseCanonicalId(canonicalId);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ExtractVersions_ParsesAndDeduplicates()
    {
        var files = new List<BackendModFile>
        {
            new() { FileId = "1", FileName = "mod-1.0.0.zip", Version = "1.0.0" },
            new() { FileId = "2", FileName = "mod-2.1.0.zip", Version = "2.1.0" },
            new() { FileId = "3", FileName = "mod-2.1.0-hotfix.zip", Version = "2.1.0" },
            new() { FileId = "4", FileName = "readme.txt", Version = null },
            new() { FileId = "5", FileName = "mod-bad.zip", Version = "not-a-version" },
        };

        var versions = NexusModsVersionProvider.ExtractVersions(files);

        versions.Should().HaveCount(2);
        versions.Should().Contain(v => v.Major == 1 && v.Minor == 0 && v.Patch == 0);
        versions.Should().Contain(v => v.Major == 2 && v.Minor == 1 && v.Patch == 0);
    }

    [Fact]
    public async Task GetAvailableVersionsAsync_QueriesBackendAndParsesVersions()
    {
        var mockBackend = new Mock<IModBackend>();
        mockBackend
            .Setup(b => b.GetModFilesAsync("1234", "skyrim", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BackendModFile>
            {
                new() { FileId = "1", Version = "1.0.0" },
                new() { FileId = "2", Version = "2.0.0" },
            });

        var provider = new NexusModsVersionProvider(mockBackend.Object, "skyrim");

        var versions = await provider.GetAvailableVersionsAsync("nexusmods:1234");

        versions.Should().HaveCount(2);
        mockBackend.Verify(b => b.GetModFilesAsync("1234", "skyrim", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetDependenciesAsync_ReturnsEmptyList()
    {
        var backend = new Mock<IModBackend>().Object;
        var provider = new NexusModsVersionProvider(backend, "skyrim");

        var deps = await provider.GetDependenciesAsync(
            "nexusmods:1234",
            new SemanticVersion(1, 0, 0));

        deps.Should().BeEmpty();
    }
}

public class GameBananaVersionProviderTests
{
    [Theory]
    [InlineData("gamebanana:9999", "9999")]
    [InlineData("12345", "12345")]
    public void ParseCanonicalId_ValidFormats(string canonicalId, string expectedModId)
    {
        var modId = GameBananaVersionProvider.ParseCanonicalId(canonicalId);
        modId.Should().Be(expectedModId);
    }

    [Theory]
    [InlineData("gamebanana:a:b")]
    [InlineData("a:b:c")]
    public void ParseCanonicalId_InvalidFormat_Throws(string canonicalId)
    {
        var act = () => GameBananaVersionProvider.ParseCanonicalId(canonicalId);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("MyMod v1.2.3", "1.2.3")]
    [InlineData("update-2.0.0-beta.1.zip", "2.0.0-beta.1")]
    [InlineData("V3.0.0 Release", "3.0.0")]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("no version here", null)]
    [InlineData("", null)]
    public void ExtractVersionFromFileName_ExtractsCorrectly(string fileName, string? expected)
    {
        var result = GameBananaVersionProvider.ExtractVersionFromFileName(fileName);
        result.Should().Be(expected);
    }

    [Fact]
    public async Task GetAvailableVersionsAsync_ExtractsVersionsFromFiles()
    {
        var mockBackend = new Mock<IModBackend>();
        mockBackend
            .Setup(b => b.GetModFilesAsync("555", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BackendModFile>
            {
                new() { FileId = "1", FileName = "mod.zip", DisplayName = "MyMod v1.0.0", Version = null },
                new() { FileId = "2", FileName = "mod.zip", DisplayName = "MyMod v2.0.0", Version = "2.0.0" },
            });

        var provider = new GameBananaVersionProvider(mockBackend.Object);

        var versions = await provider.GetAvailableVersionsAsync("gamebanana:555");

        versions.Should().HaveCount(2);
        versions.Should().Contain(v => v.Major == 1);
        versions.Should().Contain(v => v.Major == 2);
    }

    [Fact]
    public async Task GetDependenciesAsync_ReturnsEmptyList()
    {
        var backend = new Mock<IModBackend>().Object;
        var provider = new GameBananaVersionProvider(backend);

        var deps = await provider.GetDependenciesAsync(
            "gamebanana:1234",
            new SemanticVersion(1, 0, 0));

        deps.Should().BeEmpty();
    }
}

public class AggregateVersionProviderTests
{
    [Fact]
    public async Task RoutesToCorrectProvider_ByPrefix()
    {
        var nexusProvider = new Mock<IModVersionProvider>();
        nexusProvider
            .Setup(p => p.GetAvailableVersionsAsync("nexusmods:1234", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SemanticVersion> { new(1, 0, 0) });

        var gbProvider = new Mock<IModVersionProvider>();
        gbProvider
            .Setup(p => p.GetAvailableVersionsAsync("gamebanana:5678", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SemanticVersion> { new(2, 0, 0) });

        var aggregate = new AggregateVersionProvider();
        aggregate.Register("nexusmods", nexusProvider.Object);
        aggregate.Register("gamebanana", gbProvider.Object);

        var nexusVersions = await aggregate.GetAvailableVersionsAsync("nexusmods:1234");
        nexusVersions.Should().ContainSingle(v => v.Major == 1);

        var gbVersions = await aggregate.GetAvailableVersionsAsync("gamebanana:5678");
        gbVersions.Should().ContainSingle(v => v.Major == 2);
    }

    [Fact]
    public async Task FallsBackToAllProviders_WhenNoPrefix()
    {
        var provider = new Mock<IModVersionProvider>();
        provider
            .Setup(p => p.GetAvailableVersionsAsync("bare-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SemanticVersion> { new(3, 0, 0) });

        var aggregate = new AggregateVersionProvider();
        aggregate.Register("nexusmods", provider.Object);

        var versions = await aggregate.GetAvailableVersionsAsync("bare-id");
        versions.Should().ContainSingle(v => v.Major == 3);
    }

    [Fact]
    public async Task ReturnsEmpty_WhenNoProvidersMatch()
    {
        var aggregate = new AggregateVersionProvider();

        var versions = await aggregate.GetAvailableVersionsAsync("unknown:123");
        versions.Should().BeEmpty();
    }
}
