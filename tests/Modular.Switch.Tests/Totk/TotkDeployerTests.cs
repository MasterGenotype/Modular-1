using FluentAssertions;
using Modular.Switch.Totk;
using Xunit;

namespace Modular.Switch.Tests.Totk;

public sealed class TotkDeployerTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly string _output;
    private readonly string _target;

    public TotkDeployerTests()
    {
        _output = Path.GetDirectoryName(_temp.File("merged/romfs/a.bin", "merged"))![..^"/romfs".Length];
        _target = _temp.Combine("load", "0100F2C0115B6000", "TKMM");
    }

    public void Dispose() => _temp.Dispose();

    private static readonly Action<string, string> NoSymlinks =
        (_, _) => throw new IOException("symlinks not permitted");

    [Fact]
    public void Deploy_LinksTheTargetToTheMergeOutput()
    {
        var result = TotkDeployer.Deploy(_output, _target);

        result.Outcome.Should().Be(TotkDeployOutcome.Linked);
        new DirectoryInfo(_target).LinkTarget.Should().Be(_output);
        File.ReadAllText(Path.Combine(_target, "romfs", "a.bin")).Should().Be("merged");
    }

    [Fact]
    public void Deploy_Again_IsAlreadyLinked()
    {
        TotkDeployer.Deploy(_output, _target);

        TotkDeployer.Deploy(_output, _target).Outcome.Should().Be(TotkDeployOutcome.AlreadyLinked);
    }

    [Fact]
    public void Deploy_ReplacesAnEmptyFolder()
    {
        Directory.CreateDirectory(Path.Combine(_target, "romfs", "empty"));

        TotkDeployer.Deploy(_output, _target).Outcome.Should().Be(TotkDeployOutcome.Linked);
    }

    [Fact]
    public void Deploy_RefusesAFolderWithFiles_AndLeavesThemAlone()
    {
        var userFile = _temp.File("load/0100F2C0115B6000/TKMM/romfs/user.bin", "user");

        var result = TotkDeployer.Deploy(_output, _target);

        result.Outcome.Should().Be(TotkDeployOutcome.Refused);
        new DirectoryInfo(_target).LinkTarget.Should().BeNull();
        File.ReadAllText(userFile).Should().Be("user");
    }

    [Fact]
    public void Deploy_ReplacesALinkToElsewhere_WithoutTouchingWhatItPointedAt()
    {
        var elsewhere = Path.GetDirectoryName(_temp.File("old-merge/romfs/old.bin", "old"))!;
        Directory.CreateDirectory(Path.GetDirectoryName(_target)!);
        Directory.CreateSymbolicLink(_target, Path.GetDirectoryName(elsewhere)!);

        TotkDeployer.Deploy(_output, _target).Outcome.Should().Be(TotkDeployOutcome.Linked);

        new DirectoryInfo(_target).LinkTarget.Should().Be(_output);
        File.ReadAllText(Path.Combine(elsewhere, "old.bin")).Should().Be("old");
    }

    [Fact]
    public void Deploy_CopiesWhenSymlinksFail()
    {
        var result = TotkDeployer.Deploy(_output, _target, createSymlink: NoSymlinks);

        result.Outcome.Should().Be(TotkDeployOutcome.Copied);
        new DirectoryInfo(_target).LinkTarget.Should().BeNull();
        File.ReadAllText(Path.Combine(_target, "romfs", "a.bin")).Should().Be("merged");
        File.Exists(Path.Combine(_target, TotkDeployer.CopyMarkerFileName)).Should().BeTrue();
    }

    [Fact]
    public void Deploy_RefreshesItsOwnCopy()
    {
        TotkDeployer.Deploy(_output, _target, createSymlink: NoSymlinks);
        File.Delete(Path.Combine(_output, "romfs", "a.bin"));
        _temp.File("merged/romfs/b.bin", "remerged");

        TotkDeployer.Deploy(_output, _target, createSymlink: NoSymlinks).Outcome.Should().Be(TotkDeployOutcome.Copied);

        File.Exists(Path.Combine(_target, "romfs", "a.bin")).Should().BeFalse();
        File.ReadAllText(Path.Combine(_target, "romfs", "b.bin")).Should().Be("remerged");
    }

    [Fact]
    public void Deploy_ToTheMergeOutputItself_IsSkipped()
    {
        TotkDeployer.Deploy(_output, _output).Outcome.Should().Be(TotkDeployOutcome.Skipped);
        File.ReadAllText(Path.Combine(_output, "romfs", "a.bin")).Should().Be("merged");
    }
}
