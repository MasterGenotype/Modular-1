using FluentAssertions;
using Modular.Core.Dependencies;
using Modular.Core.GameDetection;
using Modular.Core.Utilities;
using Modular.Sdk.Installers;
using Xunit;

namespace Modular.Core.Tests;

/// <summary>
/// Tests for Phase 1-5 integration components.
/// </summary>
public class PathSanitizerTests
{
    [Fact]
    public void RejectsParentDirectoryTraversal()
    {
        var act = () => PathSanitizer.SanitizeEntryPath("../../../etc/passwd", "/tmp/target");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*path traversal*");
    }

    [Fact]
    public void RejectsAbsolutePaths()
    {
        var act = () => PathSanitizer.SanitizeEntryPath("/etc/passwd", "/tmp/target");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void RejectsNullBytes()
    {
        var act = () => PathSanitizer.SanitizeEntryPath("file\0name.txt", "/tmp/target");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*null byte*");
    }

    [Fact]
    public void AcceptsValidRelativePaths()
    {
        var targetDir = Path.Combine(Path.GetTempPath(), "sanitizer_test_target");
        var result = PathSanitizer.SanitizeEntryPath("mods/texture.dds", targetDir);
        result.Should().StartWith(Path.GetFullPath(targetDir));
        result.Should().EndWith("texture.dds");
    }

    [Fact]
    public void NormalizesSeparators()
    {
        var result = PathSanitizer.NormalizeSeparators("folder\\subfolder/file.txt");
        // After normalization, all separators should be the platform separator
        var expected = $"folder{Path.DirectorySeparatorChar}subfolder{Path.DirectorySeparatorChar}file.txt";
        result.Should().Be(expected);
    }

    [Fact]
    public void TrySanitize_ReturnsFalseForBadPaths()
    {
        var ok = PathSanitizer.TrySanitizeEntryPath("../../etc/passwd", "/tmp/target", out var resolved);
        ok.Should().BeFalse();
        resolved.Should().BeNull();
    }

    [Fact]
    public void TrySanitize_ReturnsTrueForGoodPaths()
    {
        var ok = PathSanitizer.TrySanitizeEntryPath("data/file.txt", "/tmp/target", out var resolved);
        ok.Should().BeTrue();
        resolved.Should().NotBeNull();
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("a/../../../etc")]
    [InlineData("foo/../../bar")]
    public void ContainsTraversalSegments_DetectsTraversal(string path)
    {
        PathSanitizer.ContainsTraversalSegments(path).Should().BeTrue();
    }

    [Theory]
    [InlineData("a/b/c")]
    [InlineData("mods/texture.dds")]
    [InlineData("")]
    public void ContainsTraversalSegments_AllowsSafePaths(string path)
    {
        PathSanitizer.ContainsTraversalSegments(path).Should().BeFalse();
    }
}

public class KeyValuesParserTests
{
    [Fact]
    public void ParsesSimpleKeyValuePairs()
    {
        var input = """
            "AppState"
            {
                "appid"     "730"
                "name"      "Counter-Strike 2"
                "installdir"    "Counter-Strike Global Offensive"
            }
            """;

        var root = KeyValuesParser.Parse(input);
        var appState = root.GetChild("AppState");

        appState.Should().NotBeNull();
        appState!.GetValue("appid").Should().Be("730");
        appState.GetValue("name").Should().Be("Counter-Strike 2");
        appState.GetValue("installdir").Should().Be("Counter-Strike Global Offensive");
    }

    [Fact]
    public void ParsesNestedBlocks()
    {
        var input = """
            "libraryfolders"
            {
                "0"
                {
                    "path"      "/home/user/.local/share/Steam"
                    "label"     ""
                }
                "1"
                {
                    "path"      "/mnt/games/SteamLibrary"
                    "label"     "Games Drive"
                }
            }
            """;

        var root = KeyValuesParser.Parse(input);
        var lib = root.GetChild("libraryfolders");

        lib.Should().NotBeNull();
        lib!.Children.Should().HaveCount(2);
        lib.Children[0].GetValue("path").Should().Be("/home/user/.local/share/Steam");
        lib.Children[1].GetValue("path").Should().Be("/mnt/games/SteamLibrary");
        lib.Children[1].GetValue("label").Should().Be("Games Drive");
    }

    [Fact]
    public void HandlesComments()
    {
        var input = """
            // This is a comment
            "AppState"
            {
                "appid"     "440" // Team Fortress 2
                "name"      "Team Fortress 2"
            }
            """;

        var root = KeyValuesParser.Parse(input);
        var appState = root.GetChild("AppState");
        appState.Should().NotBeNull();
        appState!.GetValue("appid").Should().Be("440");
    }

    [Fact]
    public void HandlesEmptyInput()
    {
        var root = KeyValuesParser.Parse("");
        root.Children.Should().BeEmpty();
        root.Values.Should().BeEmpty();
    }
}

public class OperationGraphTests
{
    [Fact]
    public void SortsIndependentOperations()
    {
        var ops = new List<FileOperation>
        {
            new() { OperationId = "a", Type = FileOperationType.Extract, DestinationPath = "file1.txt" },
            new() { OperationId = "b", Type = FileOperationType.Extract, DestinationPath = "file2.txt" },
            new() { OperationId = "c", Type = FileOperationType.Extract, DestinationPath = "file3.txt" }
        };

        var sorted = OperationGraph.Sort(ops);
        sorted.Should().NotBeNull();
        sorted.Should().HaveCount(3);
    }

    [Fact]
    public void RespectsDependencyOrder()
    {
        var ops = new List<FileOperation>
        {
            new() { OperationId = "mkdir", Type = FileOperationType.CreateDirectory, DestinationPath = "mods/" },
            new() { OperationId = "file", Type = FileOperationType.Extract, DestinationPath = "mods/test.dll", DependsOn = new List<string> { "mkdir" } }
        };

        var sorted = OperationGraph.Sort(ops);
        sorted.Should().NotBeNull();
        sorted![0].OperationId.Should().Be("mkdir");
        sorted[1].OperationId.Should().Be("file");
    }

    [Fact]
    public void DetectsCycles()
    {
        var ops = new List<FileOperation>
        {
            new() { OperationId = "a", DependsOn = new List<string> { "b" } },
            new() { OperationId = "b", DependsOn = new List<string> { "a" } }
        };

        var sorted = OperationGraph.Sort(ops);
        sorted.Should().BeNull();
    }

    [Fact]
    public void IsValid_ReturnsFalseForCycles()
    {
        var ops = new List<FileOperation>
        {
            new() { OperationId = "a", DependsOn = new List<string> { "b" } },
            new() { OperationId = "b", DependsOn = new List<string> { "a" } }
        };

        OperationGraph.IsValid(ops).Should().BeFalse();
    }

    [Fact]
    public void HandlesEmptyOperationList()
    {
        var sorted = OperationGraph.Sort(new List<FileOperation>());
        sorted.Should().NotBeNull();
        sorted.Should().BeEmpty();
    }

    [Fact]
    public void InferDirectoryDependencies_AddsDirectoryOps()
    {
        var ops = new List<FileOperation>
        {
            new() { OperationId = "f1", Type = FileOperationType.Extract, DestinationPath = "mods/textures/file.dds" }
        };

        var result = OperationGraph.InferDirectoryDependencies(ops);
        result.Count.Should().BeGreaterThan(1); // Should have added mkdir ops
        result.Should().Contain(op => op.Type == FileOperationType.CreateDirectory);
    }
}

public class EngineDetectionTests
{
    [Fact]
    public void UnityDetector_ReturnsNullForNonexistentDir()
    {
        var detector = new UnityEngineDetector();
        var result = detector.Detect("/nonexistent/path/12345");
        result.Should().BeNull();
    }

    [Fact]
    public void CompositeDetector_ReturnsNullForEmptyDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"engine_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var detector = new CompositeEngineDetector();
            var result = detector.Detect(tempDir);
            result.Should().BeNull();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void CompositeDetector_DetectsUnityEngine()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"unity_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Create Unity-like file structure
            File.WriteAllText(Path.Combine(tempDir, "UnityPlayer.dll"), "fake");
            Directory.CreateDirectory(Path.Combine(tempDir, "Game_Data"));

            var detector = new CompositeEngineDetector();
            var result = detector.Detect(tempDir);

            result.Should().NotBeNull();
            result!.EngineFamily.Should().Be("Unity");
            result.Confidence.Should().BeGreaterThan(0.5);
            result.Evidence.Should().NotBeEmpty();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void CompositeDetector_DetectsGodotEngine()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"godot_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "data.pck"), "fake");

            var detector = new CompositeEngineDetector();
            var result = detector.Detect(tempDir);

            result.Should().NotBeNull();
            result!.EngineFamily.Should().Be("Godot");
            result.Confidence.Should().BeGreaterOrEqualTo(0.8);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}

public class HashUtilityTests
{
    [Fact]
    public async Task ComputeFileHash_ProducesConsistentHash()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "hello world");
            var hash1 = await HashUtility.ComputeFileHashAsync(tempFile);
            var hash2 = await HashUtility.ComputeFileHashAsync(tempFile);
            hash1.Should().Be(hash2);
            hash1.Should().HaveLength(64); // SHA-256 = 64 hex chars
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task VerifyFileHash_MatchesComputed()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "test data");
            var hash = await HashUtility.ComputeFileHashAsync(tempFile);
            var verified = await HashUtility.VerifyFileHashAsync(tempFile, hash);
            verified.Should().BeTrue();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
