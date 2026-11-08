using System.Net;
using System.Text;
using Modular.Core.Backends.NexusMods;
using Modular.Core.Configuration;
using Modular.Core.Database;
using Modular.Core.RateLimiting;
using Xunit;

namespace Modular.Core.Tests.Backends;

/// <summary>
/// One mod's page details and files, as <c>modular info</c> shows them.
/// </summary>
public sealed class NexusModInfoTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "modular-nexus-info-tests", Guid.NewGuid().ToString("N"));

    public NexusModInfoTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task GetModInfoAsync_MapsThePageDetails()
    {
        var server = new FakeNexus
        {
            ["/v1/games/baldursgate3/mods/23675.json"] = """
                {
                  "mod_id": 23675, "name": "Mazzle's MF Radio", "summary": "An in-game music player.",
                  "description": "Line one.\n<br />[b]Line two.[/b]", "author": "Mazzle23",
                  "version": "2.0.1", "category_id": 2, "endorsement_count": 16, "mod_downloads": 11205,
                  "created_timestamp": 1783037602, "updated_timestamp": 1793850867,
                  "picture_url": "https://example.com/p.png", "contains_adult_content": false
                }
                """
        };
        var backend = CreateBackend(server);

        var mod = await backend.GetModInfoAsync("23675", "baldursgate3");

        Assert.NotNull(mod);
        Assert.Equal("23675", mod.ModId);
        Assert.Equal("Mazzle's MF Radio", mod.Name);
        Assert.Equal("2.0.1", mod.Version);
        Assert.Equal("Mazzle23", mod.Author);
        Assert.Equal(2, mod.CategoryId);
        Assert.Equal(16, mod.EndorsementCount);
        Assert.Equal(11205, mod.DownloadCount);
        Assert.Equal("Line one.\n<br />[b]Line two.[/b]", mod.Description);
        Assert.Equal(new DateTime(2026, 7, 3), mod.CreatedAt?.Date);
        Assert.Equal(new DateTime(2026, 11, 5), mod.UpdatedAt?.Date);
        Assert.False(mod.IsAdult);
        Assert.Equal("https://www.nexusmods.com/baldursgate3/mods/23675", mod.Url);
        Assert.Equal("the-key", server.LastApiKey);
    }

    [Fact]
    public async Task GetModInfoAsync_NamesAHiddenModByIdAndLeavesItsCategoryUnset()
    {
        var server = new FakeNexus
        {
            ["/v1/games/baldursgate3/mods/99.json"] = """{ "mod_id": 0, "name": null, "status": "hidden", "available": false }"""
        };
        var backend = CreateBackend(server);

        var mod = await backend.GetModInfoAsync("99", "baldursgate3");

        Assert.NotNull(mod);
        Assert.Equal("99", mod.ModId);
        Assert.Equal("Mod 99", mod.Name);
        Assert.Null(mod.CategoryId);
        Assert.Null(mod.Description);
        Assert.Null(mod.CreatedAt);
    }

    [Fact]
    public async Task GetModInfoAsync_ReturnsNull_WhenTheModIsMissing()
    {
        var backend = CreateBackend(new FakeNexus());

        Assert.Null(await backend.GetModInfoAsync("404", "baldursgate3"));
    }

    [Fact]
    public async Task GetModFilesAsync_MapsFileNotesAndArchivedFiles()
    {
        var server = new FakeNexus
        {
            ["/v1/games/baldursgate3/mods/23675/files.json"] = """
                {
                  "files": [
                    { "file_id": 134793, "name": "MF Radio", "file_name": "MF Radio-23675-2-0-1.zip", "size_kb": 279,
                      "category_id": 1, "version": "2.0.1", "uploaded_timestamp": 1793850867,
                      "description": "Installing MazzleLib will add some QOL features." },
                    { "file_id": 132885, "name": "MF Radio 2.0.0", "file_name": "a.zip", "size_kb": 278,
                      "category_id": 4, "version": "2.0.0", "uploaded_timestamp": 1791943201, "description": "" },
                    { "file_id": 126039, "name": "MF Radio 1.6.0", "file_name": "b.zip", "size_kb": 347,
                      "category_id": 7, "version": "1.6.0", "uploaded_timestamp": 1784499720 }
                  ]
                }
                """
        };
        var backend = CreateBackend(server);

        var all = await backend.GetModFilesAsync(
            "23675", "baldursgate3", new Modular.Sdk.Backends.Common.FileFilter { IncludeArchived = true });

        Assert.Equal(["main", "old_version", "archived"], all.Select(f => f.Category));
        Assert.Equal("Installing MazzleLib will add some QOL features.", all[0].Description);
        Assert.Null(all[1].Description);
        Assert.Null(all[2].Description);
    }

    private NexusModsBackend CreateBackend(FakeNexus server) =>
        new(
            new AppSettings { NexusApiKey = "the-key" },
            new NexusRateLimiter(),
            new DownloadDatabase(Path.Combine(_dir, "downloads.json")),
            new ModMetadataCache(Path.Combine(_dir, "cache.json")),
            logger: null,
            downloader: null,
            apiHandler: server);

    /// <summary>Serves fixed JSON bodies by path, and 404 for any other path.</summary>
    private sealed class FakeNexus : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _bodies = new(StringComparer.Ordinal);

        public string this[string path]
        {
            set => _bodies[path] = value;
        }

        public string? LastApiKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastApiKey = request.Headers.TryGetValues("apikey", out var keys) ? keys.Single() : null;

            var found = _bodies.TryGetValue(request.RequestUri!.AbsolutePath, out var body);
            return Task.FromResult(new HttpResponseMessage(found ? HttpStatusCode.OK : HttpStatusCode.NotFound)
            {
                Content = new StringContent(found ? body! : """{"message":"No Mod Found"}""", Encoding.UTF8, "application/json")
            });
        }
    }
}
