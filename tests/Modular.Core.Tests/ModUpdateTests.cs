using FluentAssertions;
using Modular.Core.ModLists;
using Xunit;

namespace Modular.Core.Tests;

public class ModUpdateTests
{
    private static RemoteModFile F(string id, string name, string version, string date, string category = "main") =>
        new(id, name, null, version, DateTimeOffset.Parse(date), category);

    private static readonly RemoteModFile[] Edw =
    [
        F("170501", "Expanded Dual Wielding 35626 3.6 2026-10-29T16-04Z E91D9d451.zip", "3.6", "2026-10-29T16:04:52Z", "archived"),
        F("168665", "Compatibility patch for Immersive First Person 35626 1 2026-10-26T03-44Z D2ZS2nnd6.zip", "1", "2026-10-26T03:44:53Z", "optional"),
        F("180274", "Expanded Dual Wielding 35626 3.8 2026-11-18T04-44Z JSKWSQ8qb.zip", "3.8", "2026-11-18T04:44:16Z"),
        F("170083", "Expanded Dual Wielding 35626 3.3 2026-10-28T17-49Z LACBAIvsD.zip", "3.3", "2026-10-28T17:49:08Z", "main"),
    ];

    [Fact]
    public void Newer_IsTheLatestFileOfTheSameSeriesAndCategory()
    {
        var src = new ModSourceInfo("nexusmods", "cyberpunk2077", "35626", "170083", null);
        var found = ModUpdateService.FindNewer(Edw, src)!.Value;
        found.Newer.FileId.Should().Be("180274", "3.8 replaces 3.3; the optional compatibility patch is another series");
        found.Current!.FileId.Should().Be("170083");
    }

    [Fact]
    public void NoUpdate_WhenTheInstalledFileIsTheNewest()
    {
        var src = new ModSourceInfo("nexusmods", "cyberpunk2077", "35626", "180274", null);
        ModUpdateService.FindNewer(Edw, src).Should().BeNull();
    }

    [Fact]
    public void RemovedInstalledFile_OffersTheNewestMainFile_WithANote()
    {
        var src = new ModSourceInfo("nexusmods", "cyberpunk2077", "35626", "999999", null);
        var found = ModUpdateService.FindNewer(Edw, src)!.Value;
        found.Newer.FileId.Should().Be("180274");
        found.Note.Should().Contain("no longer on the mod page");
    }

    [Fact]
    public void RemovedInstalledFile_WithADatedName_FindsTheLaterSameSeriesFile()
    {
        var src = new ModSourceInfo("nexusmods", "cyberpunk2077", "35626", null,
            "Expanded Dual Wielding 35626 3.7 2026-10-30T17-00Z Q6xF6liOW.zip");
        var found = ModUpdateService.FindNewer(Edw, src)!.Value;
        found.Newer.FileId.Should().Be("180274");
        found.Note.Should().Contain("taken off");
    }

    [Theory]
    [InlineData("Expanded Dual Wielding 35626 3.8 2026-11-18T04-44Z JSKWSQ8qb.zip", "expanded dual wielding")]
    [InlineData("Ancestral Guardian-6411-1-1-1707131389.zip", "ancestral guardian")]
    [InlineData("SBBF_v1.2.3_main.zip", "sbbf main")]
    public void Series_DropsVersionsDatesAndIds(string file, string series) =>
        ModUpdateService.Series(file).Should().Be(series);
}
