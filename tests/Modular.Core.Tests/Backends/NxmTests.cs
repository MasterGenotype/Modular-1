using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Modular.Core.Backends.NexusMods;
using Modular.Core.Configuration;
using Modular.Core.Database;
using Modular.Core.Downloads;
using Modular.Core.Exceptions;
using Modular.Core.RateLimiting;
using Modular.Sdk.Backends;
using Xunit;

namespace Modular.Core.Tests.Backends;

public sealed class NxmLinkTests
{
    [Fact]
    public void Parse_ReadsModFileLink()
    {
        var link = NxmLink.Parse(
            "nxm://cyberpunk2077/mods/107/files/12345?key=Ab-cd_9&expires=1700000000&user_id=275692990");

        Assert.Equal("cyberpunk2077", link.GameDomain);
        Assert.Equal(107, link.ModId);
        Assert.Equal(12345, link.FileId);
        Assert.Equal("Ab-cd_9", link.Key);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), link.Expires);
        Assert.Equal(275692990, link.UserId);
    }

    [Fact]
    public void Parse_AcceptsUppercaseSchemeAndGame()
    {
        var link = NxmLink.Parse("NXM://SkyrimSpecialEdition/mods/1/files/2");

        Assert.Equal("skyrimspecialedition", link.GameDomain);
        Assert.Null(link.Key);
        Assert.Null(link.Expires);
    }

    [Theory]
    [InlineData("https://www.nexusmods.com/cyberpunk2077/mods/107")]
    [InlineData("nxm://cyberpunk2077/mods/107")]
    [InlineData("nxm://cyberpunk2077/mods/abc/files/1")]
    [InlineData("nxm://cyberpunk2077/mods/107/files/1/extra")]
    [InlineData("not a url")]
    public void Parse_RejectsOtherLinks(string text)
    {
        Assert.Throws<FormatException>(() => NxmLink.Parse(text));
    }

    [Fact]
    public void Parse_RejectsCollectionLinksWithReason()
    {
        var ex = Assert.Throws<FormatException>(
            () => NxmLink.Parse("nxm://cyberpunk2077/collections/abcdef/revisions/3"));

        Assert.Contains("Collection", ex.Message);
    }

    [Fact]
    public void IsExpired_ComparesExpiry()
    {
        var link = NxmLink.Parse("nxm://g/mods/1/files/2?key=k&expires=1000");

        Assert.False(link.IsExpired(DateTimeOffset.FromUnixTimeSeconds(999)));
        Assert.True(link.IsExpired(DateTimeOffset.FromUnixTimeSeconds(1000)));
    }
}

public sealed class NexusModsNxmDownloadTests : IDisposable
{
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("pretend this is a mod archive");
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "modular-nxm-tests", Guid.NewGuid().ToString("N"));

    public NexusModsNxmDownloadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task DownloadNxmAsync_SendsLinkKeyAndRecordsDownload()
    {
        var expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var server = new FakeNexus(expectedKey: "the-key", expires);
        var (backend, database) = CreateBackend(server);

        var link = NxmLink.Parse($"nxm://cyberpunk2077/mods/107/files/555?key=the-key&expires={expires}&user_id=1");
        var (fileName, already) = await backend.DownloadNxmAsync(link, Path.Combine(_dir, "mods"));

        Assert.False(already);
        Assert.Equal("Mod-107-1-0.zip", fileName);
        var record = database.FindRecord("cyberpunk2077", 107, 555);
        Assert.NotNull(record);
        Assert.Equal(Content, await File.ReadAllBytesAsync(record!.Filepath));
        Assert.Contains(server.Requests, r => r.Contains("download_link.json") && r.Contains("key=the-key"));
    }

    [Fact]
    public async Task DownloadNxmAsync_SkipsFileAlreadyInDatabase()
    {
        var expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var server = new FakeNexus(expectedKey: "k", expires);
        var (backend, _) = CreateBackend(server);
        var link = NxmLink.Parse($"nxm://cyberpunk2077/mods/107/files/555?key=k&expires={expires}");

        await backend.DownloadNxmAsync(link, Path.Combine(_dir, "mods"));
        server.Requests.Clear();
        var (_, already) = await backend.DownloadNxmAsync(link, Path.Combine(_dir, "mods"));

        Assert.True(already);
        Assert.DoesNotContain(server.Requests, r => r.Contains("download_link.json"));
    }

    [Fact]
    public async Task DownloadNxmAsync_ReportsRefusedKey()
    {
        var expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var server = new FakeNexus(expectedKey: "right", expires);
        var (backend, _) = CreateBackend(server);
        var link = NxmLink.Parse($"nxm://cyberpunk2077/mods/107/files/555?key=wrong&expires={expires}");

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => backend.DownloadNxmAsync(link, Path.Combine(_dir, "mods")));

        Assert.Contains("refused", ex.Message);
    }

    [Fact]
    public async Task DownloadNxmAsync_RejectsExpiredLinkWithoutCallingNexus()
    {
        var server = new FakeNexus(expectedKey: "k", 1000);
        var (backend, _) = CreateBackend(server);
        var link = NxmLink.Parse("nxm://cyberpunk2077/mods/107/files/555?key=k&expires=1000");

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => backend.DownloadNxmAsync(link, Path.Combine(_dir, "mods")));

        Assert.Contains("expired", ex.Message);
        Assert.Empty(server.Requests);
    }

    private (NexusModsBackend Backend, DownloadDatabase Database) CreateBackend(FakeNexus server)
    {
        var settings = new AppSettings { NexusApiKey = "sso-key" };
        var database = new DownloadDatabase(Path.Combine(_dir, "downloads.json"));
        var backend = new NexusModsBackend(
            settings,
            new NexusRateLimiter(),
            database,
            new ModMetadataCache(Path.Combine(_dir, "cache.json")),
            logger: null,
            downloader: new ResumableDownloader(server),
            apiHandler: server);
        return (backend, database);
    }

    /// <summary>The NexusMods API and CDN for one file: mod 107, file 555.</summary>
    private sealed class FakeNexus(string expectedKey, long expires) : HttpMessageHandler
    {
        private const string CdnUrl = "https://cdn.example/Mod-107-1-0.zip";

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            lock (Requests)
                Requests.Add(uri.ToString());

            if (uri.AbsolutePath == "/v1/games/cyberpunk2077/mods/107/files.json")
            {
                var md5 = Convert.ToHexStringLower(MD5.HashData(Content));
                return Json($$"""
                    {"files":[{"file_id":555,"name":"Main","file_name":"Mod-107-1-0.zip","size_kb":1,
                      "md5":"{{md5}}","category_id":1,"version":"1.0","uploaded_timestamp":1700000000}]}
                    """);
            }

            if (uri.AbsolutePath == "/v1/games/cyberpunk2077/mods/107/files/555/download_link.json")
            {
                Assert.Equal("sso-key", request.Headers.GetValues("apikey").Single());
                var query = HttpUtility.ParseQueryString(uri.Query);
                return query["key"] == expectedKey && query["expires"] == expires.ToString()
                    ? Json($$"""[{"name":"CDN","short_name":"cdn","URI":"{{CdnUrl}}"}]""")
                    : Json("""{"message":"Provided key and expire time isn't correct for this user/file."}""",
                        HttpStatusCode.Forbidden);
            }

            if (uri.ToString() == CdnUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Content)
                });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
