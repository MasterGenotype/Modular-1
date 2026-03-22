using FluentAssertions;
using Modular.Core.Dependencies;
using Modular.Core.Metadata;
using Modular.Core.Versioning;
using Moq;
using Xunit;

namespace Modular.Core.Tests;

public class BacktrackingResolverTests
{
    /// <summary>
    /// Simple case: ModA requires ModB, both have versions available.
    /// </summary>
    [Fact]
    public async Task Resolves_SimpleLinearDependency()
    {
        var provider = new Mock<IModVersionProvider>();

        provider.Setup(p => p.GetAvailableVersionsAsync("ModA", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SemanticVersion> { new(1, 0, 0) });

        provider.Setup(p => p.GetAvailableVersionsAsync("ModB", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SemanticVersion> { new(2, 0, 0) });

        provider.Setup(p => p.GetDependenciesAsync("ModA", It.IsAny<SemanticVersion>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ModDependency>
            {
                new() { Type = DependencyType.Required, Target = new DependencyTarget { ProjectId = "ModB" } }
            });

        provider.Setup(p => p.GetDependenciesAsync("ModB", It.IsAny<SemanticVersion>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ModDependency>());

        var resolver = new BacktrackingDependencyResolver(provider.Object);
        var result = await resolver.ResolveAsync(new() { ("ModA", null) });

        result.Success.Should().BeTrue();
        result.ResolvedVersions.Should().ContainKey("ModA");
        result.ResolvedVersions.Should().ContainKey("ModB");
    }

    /// <summary>
    /// The greedy resolver fails this: ModA v2.0 requires ModC >=2.0, ModB requires ModC <2.0.
    /// But ModA v1.0 works with ModC 1.x. Backtracking should find this.
    /// </summary>
    [Fact]
    public async Task Resolves_WithBacktracking_WhenGreedyWouldFail()
    {
        var provider = new Mock<IModVersionProvider>();

        // ModA has v1.0 and v2.0
        provider.Setup(p => p.GetAvailableVersionsAsync("ModA", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SemanticVersion> { new(1, 0, 0), new(2, 0, 0) });

        // ModB has v1.0
        provider.Setup(p => p.GetAvailableVersionsAsync("ModB", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SemanticVersion> { new(1, 0, 0) });

        // ModC has v1.0 and v2.0
        provider.Setup(p => p.GetAvailableVersionsAsync("ModC", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SemanticVersion> { new(1, 0, 0), new(2, 0, 0) });

        // ModA v2.0 requires ModC >=2.0
        provider.Setup(p => p.GetDependenciesAsync("ModA", new SemanticVersion(2, 0, 0), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ModDependency>
            {
                new() { Type = DependencyType.Required, Target = new DependencyTarget { ProjectId = "ModC" }, Constraint = ">=2.0.0" }
            });

        // ModA v1.0 requires ModC >=1.0.0
        provider.Setup(p => p.GetDependenciesAsync("ModA", new SemanticVersion(1, 0, 0), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ModDependency>
            {
                new() { Type = DependencyType.Required, Target = new DependencyTarget { ProjectId = "ModC" }, Constraint = ">=1.0.0" }
            });

        // ModB v1.0 requires ModC <2.0
        provider.Setup(p => p.GetDependenciesAsync("ModB", new SemanticVersion(1, 0, 0), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ModDependency>
            {
                new() { Type = DependencyType.Required, Target = new DependencyTarget { ProjectId = "ModC" }, Constraint = "<2.0.0" }
            });

        // ModC has no dependencies
        provider.Setup(p => p.GetDependenciesAsync("ModC", It.IsAny<SemanticVersion>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ModDependency>());

        var resolver = new BacktrackingDependencyResolver(provider.Object);
        var result = await resolver.ResolveAsync(new() { ("ModA", null), ("ModB", null) });

        result.Success.Should().BeTrue();
        // Must have selected ModA v1.0 (not v2.0) to satisfy ModB's constraint on ModC
        result.ResolvedVersions["ModA"].Should().Be(new SemanticVersion(1, 0, 0));
        result.ResolvedVersions["ModC"].Major.Should().Be(1);
    }

    /// <summary>
    /// No versions available for a required mod.
    /// </summary>
    [Fact]
    public async Task Fails_WhenNoVersionsAvailable()
    {
        var provider = new Mock<IModVersionProvider>();

        provider.Setup(p => p.GetAvailableVersionsAsync("ModA", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SemanticVersion>());

        var resolver = new BacktrackingDependencyResolver(provider.Object);
        var result = await resolver.ResolveAsync(new() { ("ModA", null) });

        result.Success.Should().BeFalse();
        result.Conflicts.Should().ContainSingle(c => c.Type == ConflictType.NoVersionsAvailable);
    }

    /// <summary>
    /// Optional dependencies that fail should be reported but not block resolution.
    /// </summary>
    [Fact]
    public async Task OptionalDependency_SkippedWhenUnavailable()
    {
        var provider = new Mock<IModVersionProvider>();

        provider.Setup(p => p.GetAvailableVersionsAsync("ModA", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SemanticVersion> { new(1, 0, 0) });

        // Optional dependency has no versions.
        provider.Setup(p => p.GetAvailableVersionsAsync("OptionalMod", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SemanticVersion>());

        provider.Setup(p => p.GetDependenciesAsync("ModA", It.IsAny<SemanticVersion>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ModDependency>
            {
                new() { Type = DependencyType.Optional, Target = new DependencyTarget { ProjectId = "OptionalMod" } }
            });

        var resolver = new BacktrackingDependencyResolver(provider.Object);
        var result = await resolver.ResolveAsync(new() { ("ModA", null) }, includeOptional: true);

        result.Success.Should().BeTrue();
        result.ResolvedVersions.Should().ContainKey("ModA");
        result.ResolvedVersions.Should().NotContainKey("OptionalMod");
        result.OptionalFailures.Should().ContainSingle(f => f.CanonicalId == "OptionalMod");
    }

    /// <summary>
    /// Version constraints from root requirements are respected.
    /// </summary>
    [Fact]
    public async Task RespectsRootVersionConstraints()
    {
        var provider = new Mock<IModVersionProvider>();

        provider.Setup(p => p.GetAvailableVersionsAsync("ModA", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SemanticVersion> { new(1, 0, 0), new(2, 0, 0), new(3, 0, 0) });

        provider.Setup(p => p.GetDependenciesAsync("ModA", It.IsAny<SemanticVersion>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ModDependency>());

        var resolver = new BacktrackingDependencyResolver(provider.Object);
        var constraint = VersionRange.Parse("<3.0.0");
        var result = await resolver.ResolveAsync(new() { ("ModA", constraint) });

        result.Success.Should().BeTrue();
        result.ResolvedVersions["ModA"].Should().Be(new SemanticVersion(2, 0, 0));
    }

    /// <summary>
    /// Incompatible mods are detected.
    /// </summary>
    [Fact]
    public async Task DetectsIncompatibleMods()
    {
        var provider = new Mock<IModVersionProvider>();

        provider.Setup(p => p.GetAvailableVersionsAsync("ModA", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SemanticVersion> { new(1, 0, 0) });

        provider.Setup(p => p.GetAvailableVersionsAsync("ModB", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SemanticVersion> { new(1, 0, 0) });

        // ModA is incompatible with ModB
        provider.Setup(p => p.GetDependenciesAsync("ModA", It.IsAny<SemanticVersion>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ModDependency>
            {
                new() { Type = DependencyType.Incompatible, Target = new DependencyTarget { ProjectId = "ModB" } }
            });

        provider.Setup(p => p.GetDependenciesAsync("ModB", It.IsAny<SemanticVersion>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ModDependency>());

        var resolver = new BacktrackingDependencyResolver(provider.Object);
        var result = await resolver.ResolveAsync(new() { ("ModA", null), ("ModB", null) });

        // ModA v1.0 is incompatible with ModB — all versions of ModA fail
        result.Success.Should().BeFalse();
    }
}
