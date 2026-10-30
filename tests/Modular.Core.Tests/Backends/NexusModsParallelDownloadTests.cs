using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Modular.Core.Backends.NexusMods;
using Modular.Core.Configuration;
using Modular.Core.Database;
using Modular.Core.Downloads;
using Modular.Core.RateLimiting;
using Modular.Sdk.Backends;
using Modular.Sdk.Backends.Common;
using Xunit;

namespace Modular.Core.Tests.Backends;

public sealed partial class NexusModsParallelDownloadTests : IDisposable
{
    private const int ModCount = 5;
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "modular-parallel-tests", Guid.NewGuid().ToString("N"));

    public NexusModsParallelDownloadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task DownloadModsAsync_RunsUpToMaxConcurrencyFilesAtOnce(int maxConcurrency)
    {
        var server = new FakeNexus();
        var database = new DownloadDatabase(Path.Combine(_dir, "downloads.json"));
        var backend = new NexusModsBackend(
            new AppSettings { NexusApiKey = "key" },
            new NexusRateLimiter(),
            database,
            new ModMetadataCache(Path.Combine(_dir, "cache.json")),
            logger: null,
            downloader: new ResumableDownloader(server),
            apiHandler: server);

        await backend.DownloadModsAsync(
            Path.Combine(_dir, "mods"),
            "game",
            new DownloadOptions { MaxConcurrency = maxConcurrency, Filter = FileFilter.MainAndOptional });

        Assert.Equal(maxConcurrency, server.MostAtOnce);
        for (var mod = 1; mod <= ModCount; mod++)
            Assert.True(database.IsDownloaded("game", mod, mod * 10));
    }

    /// <summary>
    /// NexusMods with <see cref="ModCount"/> tracked mods of one file each. Each CDN
    /// transfer is held briefly, so overlapping ones are seen.
    /// </summary>
    private sealed partial class FakeNexus : HttpMessageHandler
    {
        private int _atOnce;
        private int _mostAtOnce;

        public int MostAtOnce => Volatile.Read(ref _mostAtOnce);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/v1/user/tracked_mods.json")
                return Json("[" + string.Join(",", Enumerable.Range(1, ModCount).Select(m =>
                    $$"""{"mod_id":{{m}},"domain_name":"game","name":"Mod {{m}}"}"""))+ "]");

            if (ModInfo().Match(path) is { Success: true } info)
                return Json($$"""{"mod_id":{{info.Groups[1].Value}},"name":"Mod {{info.Groups[1].Value}}"}""");

            if (Files().Match(path) is { Success: true } files)
            {
                var mod = int.Parse(files.Groups[1].Value);
                return Json($$"""
                    {"files":[{"file_id":{{mod * 10}},"name":"Main","file_name":"mod{{mod}}.zip","size_kb":1,
                      "category_id":1,"version":"1","uploaded_timestamp":1700000000}]}
                    """);
            }

            if (Link().Match(path) is { Success: true } link)
                return Json($$"""[{"name":"CDN","short_name":"cdn","URI":"https://cdn.example/{{link.Groups[1].Value}}"}]""");

            if (request.RequestUri.Host == "cdn.example")
            {
                var now = Interlocked.Increment(ref _atOnce);
                int seen;
                while (now > (seen = Volatile.Read(ref _mostAtOnce)) &&
                       Interlocked.CompareExchange(ref _mostAtOnce, now, seen) != seen)
                {
                }

                await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
                Interlocked.Decrement(ref _atOnce);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes(path))
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        [GeneratedRegex(@"^/v1/games/game/mods/(\d+)\.json$")]
        private static partial Regex ModInfo();

        [GeneratedRegex(@"^/v1/games/game/mods/(\d+)/files\.json$")]
        private static partial Regex Files();

        [GeneratedRegex(@"^/v1/games/game/mods/\d+/files/(\d+)/download_link\.json$")]
        private static partial Regex Link();
    }
}
