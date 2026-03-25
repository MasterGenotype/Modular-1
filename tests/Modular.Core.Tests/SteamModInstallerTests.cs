using System.IO.Compression;
using System.Security.Cryptography;
using FluentAssertions;
using Modular.Core.Installers.Steam;
using Xunit;

namespace Modular.Core.Tests;

public class SteamConstraintSolverTests
{
    private readonly SteamConstraintSolver _solver = new();

    [Fact]
    public void Resolve_SimpleDependencyChain_ReturnsCorrectOrder()
    {
        // ModA depends on ModB, ModB depends on ModC
        var mods = new List<SteamModMetadata>
        {
            new()
            {
                Name = "ModA", TargetGame = "GameX", Version = "1.0.0", Checksum = "abc",
                Dependencies = new List<SteamModDependency> { SteamModDependency.Required("ModB") }
            },
            new()
            {
                Name = "ModB", TargetGame = "GameX", Version = "1.2.0", Checksum = "def",
                Dependencies = new List<SteamModDependency> { SteamModDependency.Required("ModC") }
            },
            new()
            {
                Name = "ModC", TargetGame = "GameX", Version = "2.0.0", Checksum = "ghi",
                Dependencies = new List<SteamModDependency>()
            }
        };

        var result = _solver.Resolve(mods);

        result.Success.Should().BeTrue();
        result.InstallOrder.Should().HaveCount(3);

        // ModC should come before ModB, ModB before ModA
        var names = result.InstallOrder.Select(m => m.Name).ToList();
        names.IndexOf("ModC").Should().BeLessThan(names.IndexOf("ModB"));
        names.IndexOf("ModB").Should().BeLessThan(names.IndexOf("ModA"));
    }

    [Fact]
    public void Resolve_NoDependencies_ReturnsAllMods()
    {
        var mods = new List<SteamModMetadata>
        {
            new() { Name = "ModA", TargetGame = "GameX", Version = "1.0.0" },
            new() { Name = "ModB", TargetGame = "GameX", Version = "2.0.0" }
        };

        var result = _solver.Resolve(mods);

        result.Success.Should().BeTrue();
        result.InstallOrder.Should().HaveCount(2);
    }

    [Fact]
    public void Resolve_CircularDependency_Fails()
    {
        var mods = new List<SteamModMetadata>
        {
            new()
            {
                Name = "ModA", TargetGame = "GameX", Version = "1.0.0",
                Dependencies = new List<SteamModDependency> { SteamModDependency.Required("ModB") }
            },
            new()
            {
                Name = "ModB", TargetGame = "GameX", Version = "1.0.0",
                Dependencies = new List<SteamModDependency> { SteamModDependency.Required("ModA") }
            }
        };

        var result = _solver.Resolve(mods);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Contains("Circular dependency"));
    }

    [Fact]
    public void Resolve_MissingRequiredDependency_Fails()
    {
        var mods = new List<SteamModMetadata>
        {
            new()
            {
                Name = "ModA", TargetGame = "GameX", Version = "1.0.0",
                Dependencies = new List<SteamModDependency> { SteamModDependency.Required("ModMissing") }
            }
        };

        var result = _solver.Resolve(mods);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Contains("Missing required dependency"));
    }

    [Fact]
    public void Resolve_MissingOptionalDependency_SucceedsWithWarning()
    {
        var mods = new List<SteamModMetadata>
        {
            new()
            {
                Name = "ModA", TargetGame = "GameX", Version = "1.0.0",
                Dependencies = new List<SteamModDependency> { SteamModDependency.Optional("ModOptional") }
            }
        };

        var result = _solver.Resolve(mods);

        result.Success.Should().BeTrue();
        result.Warnings.Should().ContainSingle(w => w.Contains("Optional dependency"));
        result.InstallOrder.Should().ContainSingle(m => m.Name == "ModA");
    }

    [Fact]
    public void Resolve_VersionConstraintSatisfied_Succeeds()
    {
        var mods = new List<SteamModMetadata>
        {
            new()
            {
                Name = "ModA", TargetGame = "GameX", Version = "1.0.0",
                Dependencies = new List<SteamModDependency>
                    { SteamModDependency.Required("ModB", ">=1.0.0") }
            },
            new()
            {
                Name = "ModB", TargetGame = "GameX", Version = "1.2.0",
                Dependencies = new List<SteamModDependency>()
            }
        };

        var result = _solver.Resolve(mods);

        result.Success.Should().BeTrue();
        result.InstallOrder.Should().HaveCount(2);
    }

    [Fact]
    public void Resolve_VersionConstraintNotSatisfied_Fails()
    {
        var mods = new List<SteamModMetadata>
        {
            new()
            {
                Name = "ModA", TargetGame = "GameX", Version = "1.0.0",
                Dependencies = new List<SteamModDependency>
                    { SteamModDependency.Required("ModB", ">=2.0.0") }
            },
            new()
            {
                Name = "ModB", TargetGame = "GameX", Version = "1.2.0",
                Dependencies = new List<SteamModDependency>()
            }
        };

        var result = _solver.Resolve(mods);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Contains("Version conflict"));
    }

    [Fact]
    public void Resolve_ConditionalDependency_ActiveCondition_Resolves()
    {
        var mods = new List<SteamModMetadata>
        {
            new()
            {
                Name = "ModA", TargetGame = "GameX", Version = "1.0.0",
                Dependencies = new List<SteamModDependency>
                    { SteamModDependency.Conditional("ModB", "linux") }
            },
            new()
            {
                Name = "ModB", TargetGame = "GameX", Version = "1.0.0",
                Dependencies = new List<SteamModDependency>()
            }
        };

        var result = _solver.Resolve(mods, new HashSet<string> { "linux" });

        result.Success.Should().BeTrue();
        result.InstallOrder.Should().HaveCount(2);
        var names = result.InstallOrder.Select(m => m.Name).ToList();
        names.IndexOf("ModB").Should().BeLessThan(names.IndexOf("ModA"));
    }

    [Fact]
    public void Resolve_ConditionalDependency_InactiveCondition_IgnoresDep()
    {
        var mods = new List<SteamModMetadata>
        {
            new()
            {
                Name = "ModA", TargetGame = "GameX", Version = "1.0.0",
                Dependencies = new List<SteamModDependency>
                    { SteamModDependency.Conditional("ModMissing", "windows") }
            }
        };

        // "windows" condition is not active, so the missing dependency is ignored
        var result = _solver.Resolve(mods, new HashSet<string> { "linux" });

        result.Success.Should().BeTrue();
        result.InstallOrder.Should().ContainSingle(m => m.Name == "ModA");
    }

    [Fact]
    public void Resolve_DuplicateModNames_Fails()
    {
        var mods = new List<SteamModMetadata>
        {
            new() { Name = "ModA", TargetGame = "GameX", Version = "1.0.0" },
            new() { Name = "ModA", TargetGame = "GameX", Version = "2.0.0" }
        };

        var result = _solver.Resolve(mods);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Contains("Duplicate mod name"));
    }

    [Fact]
    public void Resolve_DiamondDependency_Succeeds()
    {
        // ModA -> ModB, ModA -> ModC, ModB -> ModD, ModC -> ModD
        var mods = new List<SteamModMetadata>
        {
            new()
            {
                Name = "ModA", TargetGame = "GameX", Version = "1.0.0",
                Dependencies = new List<SteamModDependency>
                {
                    SteamModDependency.Required("ModB"),
                    SteamModDependency.Required("ModC")
                }
            },
            new()
            {
                Name = "ModB", TargetGame = "GameX", Version = "1.0.0",
                Dependencies = new List<SteamModDependency> { SteamModDependency.Required("ModD") }
            },
            new()
            {
                Name = "ModC", TargetGame = "GameX", Version = "1.0.0",
                Dependencies = new List<SteamModDependency> { SteamModDependency.Required("ModD") }
            },
            new()
            {
                Name = "ModD", TargetGame = "GameX", Version = "1.0.0",
                Dependencies = new List<SteamModDependency>()
            }
        };

        var result = _solver.Resolve(mods);

        result.Success.Should().BeTrue();
        result.InstallOrder.Should().HaveCount(4);

        // ModD must come before ModB and ModC, which must come before ModA
        var names = result.InstallOrder.Select(m => m.Name).ToList();
        names.IndexOf("ModD").Should().BeLessThan(names.IndexOf("ModB"));
        names.IndexOf("ModD").Should().BeLessThan(names.IndexOf("ModC"));
        names.IndexOf("ModB").Should().BeLessThan(names.IndexOf("ModA"));
        names.IndexOf("ModC").Should().BeLessThan(names.IndexOf("ModA"));
    }

    [Fact]
    public void Resolve_CaretVersionConstraint_Succeeds()
    {
        var mods = new List<SteamModMetadata>
        {
            new()
            {
                Name = "ModA", TargetGame = "GameX", Version = "1.0.0",
                Dependencies = new List<SteamModDependency>
                    { SteamModDependency.Required("ModB", "^1.0.0") }
            },
            new()
            {
                Name = "ModB", TargetGame = "GameX", Version = "1.5.0",
                Dependencies = new List<SteamModDependency>()
            }
        };

        var result = _solver.Resolve(mods);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public void Resolve_CaretVersionConstraint_MajorMismatch_Fails()
    {
        var mods = new List<SteamModMetadata>
        {
            new()
            {
                Name = "ModA", TargetGame = "GameX", Version = "1.0.0",
                Dependencies = new List<SteamModDependency>
                    { SteamModDependency.Required("ModB", "^1.0.0") }
            },
            new()
            {
                Name = "ModB", TargetGame = "GameX", Version = "2.0.0",
                Dependencies = new List<SteamModDependency>()
            }
        };

        var result = _solver.Resolve(mods);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Contains("Version conflict"));
    }
}

public class SteamModInstallerTests
{
    [Fact]
    public async Task DetectAsync_ZipFile_CanHandle()
    {
        var archivePath = CreateTestZipArchive("test-steam-detect");

        try
        {
            var installer = new SteamModInstaller();
            var result = await installer.DetectAsync(archivePath);

            result.CanHandle.Should().BeTrue();
            result.Confidence.Should().BeGreaterThan(0);
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task DetectAsync_UnsupportedFormat_CannotHandle()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "test-steam-detect.exe");
        await File.WriteAllTextAsync(tempFile, "not an archive");

        try
        {
            var installer = new SteamModInstaller();
            var result = await installer.DetectAsync(tempFile);

            result.CanHandle.Should().BeFalse();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_ZipFile_CreatesOperations()
    {
        var archivePath = CreateTestZipArchive("test-steam-analyze");

        try
        {
            var installer = new SteamModInstaller();
            var context = new Modular.Sdk.Installers.InstallContext
            {
                GameDirectory = Path.GetTempPath()
            };

            var plan = await installer.AnalyzeAsync(archivePath, context);

            plan.InstallerId.Should().Be("steam-mod");
            plan.Operations.Should().NotBeEmpty();
            plan.TotalBytes.Should().BeGreaterThan(0);
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task InstallAsync_ZipFile_ExtractsToTarget()
    {
        var archivePath = CreateTestZipArchive("test-steam-install");
        var targetDir = Path.Combine(Path.GetTempPath(), "test-steam-target-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(targetDir);
            var installer = new SteamModInstaller();
            var context = new Modular.Sdk.Installers.InstallContext
            {
                GameDirectory = targetDir
            };

            var plan = await installer.AnalyzeAsync(archivePath, context);
            // Override the source path so commit goes to our target
            var result = await installer.InstallAsync(plan);

            result.Success.Should().BeTrue();
            result.InstalledFiles.Should().NotBeEmpty();
        }
        finally
        {
            File.Delete(archivePath);
            if (Directory.Exists(targetDir))
                Directory.Delete(targetDir, recursive: true);
        }
    }

    [Fact]
    public async Task InstallModsAsync_WithDependencies_InstallsInOrder()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "test-steam-batch-" + Guid.NewGuid().ToString("N")[..8]);
        var gameDir = Path.Combine(tempDir, "game");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(gameDir);

        var archiveA = CreateTestZipArchive(Path.Combine(tempDir, "ModA"), "fileA.txt", "ContentA");
        var archiveB = CreateTestZipArchive(Path.Combine(tempDir, "ModB"), "fileB.txt", "ContentB");
        var checksumA = ComputeSha256(archiveA);
        var checksumB = ComputeSha256(archiveB);

        try
        {
            var mods = new List<SteamModMetadata>
            {
                new()
                {
                    Name = "ModA", TargetGame = "GameX", Version = "1.0.0",
                    Checksum = checksumA, ArchivePath = archiveA,
                    Dependencies = new List<SteamModDependency> { SteamModDependency.Required("ModB") }
                },
                new()
                {
                    Name = "ModB", TargetGame = "GameX", Version = "1.2.0",
                    Checksum = checksumB, ArchivePath = archiveB,
                    Dependencies = new List<SteamModDependency>()
                }
            };

            var installer = new SteamModInstaller();
            var result = await installer.InstallModsAsync(mods, gameDir);

            result.Success.Should().BeTrue();
            result.InstallOrder.Should().Equal("ModB", "ModA");
            result.InstalledFiles.Should().NotBeEmpty();

            // Verify files were actually installed
            File.Exists(Path.Combine(gameDir, "fileA.txt")).Should().BeTrue();
            File.Exists(Path.Combine(gameDir, "fileB.txt")).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task InstallModsAsync_FileConflict_Aborts()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "test-steam-conflict-" + Guid.NewGuid().ToString("N")[..8]);
        var gameDir = Path.Combine(tempDir, "game");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(gameDir);

        // Both mods contain the same file
        var archiveA = CreateTestZipArchive(Path.Combine(tempDir, "ModA"), "shared.txt", "ContentA");
        var archiveB = CreateTestZipArchive(Path.Combine(tempDir, "ModB"), "shared.txt", "ContentB");
        var checksumA = ComputeSha256(archiveA);
        var checksumB = ComputeSha256(archiveB);

        try
        {
            var mods = new List<SteamModMetadata>
            {
                new()
                {
                    Name = "ModA", TargetGame = "GameX", Version = "1.0.0",
                    Checksum = checksumA, ArchivePath = archiveA,
                    Dependencies = new List<SteamModDependency>()
                },
                new()
                {
                    Name = "ModB", TargetGame = "GameX", Version = "1.0.0",
                    Checksum = checksumB, ArchivePath = archiveB,
                    Dependencies = new List<SteamModDependency>()
                }
            };

            var installer = new SteamModInstaller();
            var result = await installer.InstallModsAsync(mods, gameDir);

            result.Success.Should().BeFalse();
            result.FileConflicts.Should().NotBeEmpty();
            result.Errors.Should().Contain(e => e.Contains("File conflict"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task InstallModsAsync_MissingArchive_Aborts()
    {
        var gameDir = Path.Combine(Path.GetTempPath(), "test-steam-missing-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            var mods = new List<SteamModMetadata>
            {
                new()
                {
                    Name = "ModA", TargetGame = "GameX", Version = "1.0.0",
                    Checksum = "abc", ArchivePath = "/nonexistent/path.zip",
                    Dependencies = new List<SteamModDependency>()
                }
            };

            var installer = new SteamModInstaller();
            var result = await installer.InstallModsAsync(mods, gameDir);

            result.Success.Should().BeFalse();
            result.Errors.Should().Contain(e => e.Contains("Archive not found"));
        }
        finally
        {
            if (Directory.Exists(gameDir))
                Directory.Delete(gameDir, recursive: true);
        }
    }

    [Fact]
    public async Task InstallModsAsync_BackupAndRollback_Works()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "test-steam-rollback-" + Guid.NewGuid().ToString("N")[..8]);
        var gameDir = Path.Combine(tempDir, "game");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(gameDir);

        // Pre-populate a file in the game directory
        var existingFile = Path.Combine(gameDir, "existing.txt");
        await File.WriteAllTextAsync(existingFile, "original content");

        // Create a mod that overwrites the existing file
        var archive = CreateTestZipArchive(Path.Combine(tempDir, "ModOverwrite"), "existing.txt", "new content");

        // Compute the real SHA256 checksum of the archive
        string checksum;
        using (var hashStream = File.OpenRead(archive))
            checksum = Convert.ToHexString(SHA256.HashData(hashStream)).ToLowerInvariant();

        try
        {
            var mods = new List<SteamModMetadata>
            {
                new()
                {
                    Name = "ModOverwrite", TargetGame = "GameX", Version = "1.0.0",
                    Checksum = checksum, ArchivePath = archive,
                    Dependencies = new List<SteamModDependency>()
                }
            };

            var installer = new SteamModInstaller();
            var result = await installer.InstallModsAsync(mods, gameDir);

            result.Success.Should().BeTrue();
            result.BackedUpFiles.Should().NotBeEmpty();
            result.BackupDirectory.Should().NotBeNull();

            // Verify the file was overwritten
            File.ReadAllText(existingFile).Should().Be("new content");

            // Now rollback
            installer.Rollback(result.BackupDirectory!, gameDir, result.InstalledFiles);

            // Verify original content restored
            File.ReadAllText(existingFile).Should().Be("original content");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void VerifyChecksum_EmptyChecksum_ReturnsTrue()
    {
        var mod = new SteamModMetadata { Name = "Test", Checksum = "" };
        SteamModInstaller.VerifyChecksum(mod).Should().BeTrue();
    }

    [Fact]
    public void VerifyChecksum_MatchingChecksum_ReturnsTrue()
    {
        var archivePath = CreateTestZipArchive("test-checksum-match");
        try
        {
            using var stream = File.OpenRead(archivePath);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

            var mod = new SteamModMetadata { Name = "Test", Checksum = hash, ArchivePath = archivePath };
            SteamModInstaller.VerifyChecksum(mod).Should().BeTrue();
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public void VerifyChecksum_MismatchedChecksum_ReturnsFalse()
    {
        var archivePath = CreateTestZipArchive("test-checksum-mismatch");
        try
        {
            var mod = new SteamModMetadata { Name = "Test", Checksum = "0000000000000000000000000000000000000000000000000000000000000000", ArchivePath = archivePath };
            SteamModInstaller.VerifyChecksum(mod).Should().BeFalse();
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task ExtractToStagingAsync_ZipFormat_Extracts()
    {
        var archivePath = CreateTestZipArchive("test-steam-extract");
        var stagingDir = Path.Combine(Path.GetTempPath(), "test-staging-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(stagingDir);
            var installer = new SteamModInstaller();
            await installer.ExtractToStagingAsync(archivePath, stagingDir);

            Directory.GetFiles(stagingDir, "*", SearchOption.AllDirectories).Should().NotBeEmpty();
        }
        finally
        {
            File.Delete(archivePath);
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, recursive: true);
        }
    }

    /// <summary>
    /// Helper to create a test ZIP archive with a single file.
    /// </summary>
    private static string CreateTestZipArchive(
        string namePrefix,
        string fileName = "test.txt",
        string content = "test content")
    {
        var archivePath = namePrefix.EndsWith(".zip") ? namePrefix : namePrefix + ".zip";
        var archiveDir = Path.GetDirectoryName(archivePath);
        if (!string.IsNullOrEmpty(archiveDir) && !Directory.Exists(archiveDir))
            Directory.CreateDirectory(archiveDir);

        using var stream = new FileStream(archivePath, FileMode.Create);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(fileName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);

        return archivePath;
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

public class SteamModMetadataTests
{
    [Fact]
    public void GetSemanticVersion_ValidVersion_Parses()
    {
        var mod = new SteamModMetadata { Version = "1.2.3" };
        var version = mod.GetSemanticVersion();

        version.Major.Should().Be(1);
        version.Minor.Should().Be(2);
        version.Patch.Should().Be(3);
    }

    [Fact]
    public void SteamModDependency_FactoryMethods_CreateCorrectly()
    {
        var required = SteamModDependency.Required("ModA", ">=1.0.0");
        required.ModName.Should().Be("ModA");
        required.VersionConstraint.Should().Be(">=1.0.0");
        required.IsOptional.Should().BeFalse();
        required.Condition.Should().BeNull();

        var optional = SteamModDependency.Optional("ModB");
        optional.IsOptional.Should().BeTrue();

        var conditional = SteamModDependency.Conditional("ModC", "linux", "^2.0.0");
        conditional.Condition.Should().Be("linux");
        conditional.VersionConstraint.Should().Be("^2.0.0");
    }
}
