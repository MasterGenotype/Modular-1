using System.Text.Json;
using FluentAssertions;
using Modular.Core.Dependencies.Conventions;
using Xunit;

namespace Modular.Core.Tests.Dependencies;

public sealed class RecordSourceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("modular-record-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void SetRecordSource_KeepsWhatTheRecordAlreadyHolds()
    {
        var archive = Path.Combine(_dir, "scythereal_2ee307ff-9ee2-b21e-ganq.zip");
        File.WriteAllText(archive + ".modular.json", """
            {"Backend": null, "ModId": null, "GameDomain": null, "FileId": null,
             "Released": {"FileName": "scythereal_2ee307ff-9ee2-b21e-ganq.zip", "Size": 3155238, "Sha256": "6dfe"}}
            """);

        DependencyConventions.SetRecordSource(archive, new DeclaredSource("modio", "4898625", "6715", "5555"));

        var source = DependencyConventions.ReadDownloadRecord(archive).Source!;
        source.Should().Be(new DeclaredSource("modio", "4898625", "6715", "5555"));
        using var doc = JsonDocument.Parse(File.ReadAllText(archive + ".modular.json"));
        doc.RootElement.GetProperty("Released").GetProperty("Size").GetInt64().Should().Be(3155238, "the released archive's details stay");
    }

    [Fact]
    public void SetRecordSource_CreatesARecordWhenThereIsNone()
    {
        var archive = Path.Combine(_dir, "mod.zip");
        DependencyConventions.SetRecordSource(archive, new DeclaredSource("nexusmods", "944", "baldursgate3", "8910"));
        DependencyConventions.ReadDownloadRecord(archive).Source!.FileId.Should().Be("8910");
    }
}
