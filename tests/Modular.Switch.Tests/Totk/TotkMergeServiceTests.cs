using FluentAssertions;
using Modular.Core.Configuration;
using Modular.Switch.Totk;
using Xunit;

namespace Modular.Switch.Tests.Totk;

/// <summary>
/// The merge service's file handling. No game dump is configured, so a merge clears and
/// snapshots the output and then fails opening the game, which is all these need.
/// </summary>
[Collection(TkSharpCollection.Name)]
public sealed class TotkMergeServiceTests : IDisposable
{
    private static readonly string[] Targets = ["romfs/Pack/a.bin", "romfslite/b.bin", "exefs/c.ips", "cheats/d.txt", "romfs_metadata.bin"];
    private static readonly string[] Others = ["user-notes.txt", "extras/e.bin", "romfs.bak/f.bin", "subsdk9", "exefs_patches/TKMM/g.ips"];

    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    private TotkMergeService NewService(TotkSettings? settings = null) =>
        new(settings ?? new TotkSettings(), new TotkPaths(_temp.Combine("totk")));

    private string NewOutput(string name = "out")
    {
        foreach (var file in Targets.Concat(Others))
            _temp.File(Path.Combine(name, file), file);
        return _temp.Combine(name);
    }

    [Fact]
    public async Task Merge_DeletesOnlyTheFiveMergeTargets()
    {
        var output = NewOutput();
        var service = NewService(new TotkSettings { MergeOutput = output });

        var merge = async () => await service.MergeAsync();

        await merge.Should().ThrowAsync<Exception>("no game dump is configured");
        foreach (var target in new[] { "romfs", "romfslite", "exefs", "cheats", "romfs_metadata.bin" })
            Path.Combine(output, target).Should().Match(p => !File.Exists(p) && !Directory.Exists(p));
        foreach (var other in Others)
            File.ReadAllText(Path.Combine(output, other)).Should().Be(other);
    }

    [Fact]
    public async Task Merge_SnapshotsTheMergeTargetsFirst_AndRollbackPutsThemBack()
    {
        var output = NewOutput();
        var service = NewService(new TotkSettings { MergeOutput = output });
        var merge = async () => await service.MergeAsync();
        await merge.Should().ThrowAsync<Exception>();
        File.WriteAllText(Path.Combine(output, "user-notes.txt"), "edited after the merge");

        service.RollbackMerge().Should().BeTrue();

        foreach (var target in Targets)
            File.ReadAllText(Path.Combine(output, target)).Should().Be(target);
        File.ReadAllText(Path.Combine(output, "user-notes.txt")).Should().Be("edited after the merge");
    }

    [Fact]
    public void Rollback_WithoutASnapshotOfThisOutput_ChangesNothing()
    {
        var output = NewOutput();
        var service = NewService(new TotkSettings { MergeOutput = output });

        service.RollbackMerge().Should().BeFalse();

        foreach (var file in Targets.Concat(Others))
            File.Exists(Path.Combine(output, file)).Should().BeTrue();
    }

    [Fact]
    public void ChangingTheMergeOutput_CreatesTheNewFolder_AndNeverDeletesEither()
    {
        var oldOutput = NewOutput("old");
        var newOutput = NewOutput("new");
        var missing = _temp.Combine("missing", "merged");
        var settings = new TotkSettings { MergeOutput = oldOutput };
        var service = NewService(settings);

        service.SetMergeOutput(newOutput);
        service.MergedOutputFolder.Should().Be(newOutput);
        service.SetMergeOutput(missing);
        service.SetMergeOutput(null);

        Directory.Exists(missing).Should().BeTrue();
        service.MergedOutputFolder.Should().Be(_temp.Combine("totk", "merged"));
        settings.MergeOutput.Should().BeNull();
        foreach (var file in Targets.Concat(Others))
        {
            File.ReadAllText(Path.Combine(oldOutput, file)).Should().Be(file);
            File.ReadAllText(Path.Combine(newOutput, file)).Should().Be(file);
        }
    }

    [Fact]
    public void Profiles_CanBeCreatedAndSelected()
    {
        var service = NewService(new TotkSettings { MergeOutput = _temp.Combine("out") });

        var profile = service.CreateProfile("Testing");
        service.SelectProfile(profile);

        service.CurrentProfile.Should().BeSameAs(profile);
        service.FindProfile("testing").Should().BeSameAs(profile);
        var duplicate = () => service.CreateProfile("TESTING");
        duplicate.Should().Throw<InvalidOperationException>();
    }
}
