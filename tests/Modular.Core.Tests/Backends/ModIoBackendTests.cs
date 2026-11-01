using System.Net;
using System.Text;
using FluentAssertions;
using Modular.Core.Backends;
using Modular.Core.Backends.ModIo;
using Modular.Core.Configuration;
using Modular.Sdk.Backends;
using Modular.Sdk.Backends.Common;
using Xunit;

namespace Modular.Core.Tests.Backends;

public class ModIoBackendTests
{
    private const long Bg3 = 6715;

    // ── configuration ───────────────────────────────────────────────────────

    [Fact]
    public void Identity_IsModIo()
    {
        var (backend, _) = Create();

        backend.Id.Should().Be(BackendIds.ModIo);
        backend.DisplayName.Should().Be("mod.io");
        backend.Capabilities.Should().HaveFlag(BackendCapabilities.Search);
        backend.Capabilities.Should().HaveFlag(BackendCapabilities.Requirements);
        backend.Capabilities.Should().HaveFlag(BackendCapabilities.Md5Verification);

        // A game prompt in the CLI would ignore the configured games.
        backend.Capabilities.Should().NotHaveFlag(BackendCapabilities.GameDomains);
    }

    [Fact]
    public void ValidateConfiguration_WithoutCredentials_ReportsIt()
    {
        var (backend, _) = Create(new AppSettings());

        backend.ValidateConfiguration().Should().ContainSingle()
            .Which.Should().Contain("modio_api_key");
    }

    [Fact]
    public void ValidateConfiguration_WithAnApiKey_IsValid()
    {
        var (backend, _) = Create(new AppSettings { ModIoApiKey = "key" });

        backend.ValidateConfiguration().Should().BeEmpty();
    }

    [Fact]
    public void ResolveGameIds_RejectsAGameName()
    {
        var (backend, _) = Create();

        var act = () => backend.ResolveGameIds("baldursgate3");

        act.Should().Throw<ArgumentException>().WithMessage("*6715*");
    }

    // ── requests ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Requests_GoToTheGamesOwnHostWithTheApiKeyInTheQuery()
    {
        var (backend, server) = Create(new AppSettings { ModIoApiKey = "secret", ModIoGameIds = [6715] });
        server.On("/v1/games/6715/mods/42", Mod(42, "Party Limit"));

        await backend.GetModInfoAsync("42");

        var request = server.Requests.Should().ContainSingle().Subject;
        request.Uri.Host.Should().Be("g-6715.modapi.io");
        request.Uri.Query.Should().Contain("api_key=secret");
        request.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task Requests_WithAnAccessToken_SendABearerTokenAndNoApiKey()
    {
        var (backend, server) = Create(new AppSettings
        {
            ModIoApiKey = "secret",
            ModIoAccessToken = "token",
            ModIoGameIds = [6715]
        });
        server.On("/v1/games/6715/mods/42", Mod(42, "Party Limit"));

        await backend.GetModInfoAsync("42");

        var request = server.Requests.Should().ContainSingle().Subject;
        request.Authorization.Should().Be("Bearer token");
        request.Uri.Query.Should().NotContain("api_key");
    }

    [Fact]
    public async Task Requests_NameTheConfiguredPlatform()
    {
        // A file approved for Windows only is hidden from a request that names no platform.
        var (backend, server) = Create(new AppSettings
        {
            ModIoAccessToken = "token",
            ModIoGameIds = [6715],
            ModIoPlatform = "windows"
        });
        server.On("/v1/games/6715/mods/42", Mod(42, "Party Limit"));

        await backend.GetModInfoAsync("42");

        server.Requests.Single().Platform.Should().Be("windows");
    }

    [Fact]
    public async Task Requests_WithNoPlatformConfigured_NameNone()
    {
        var (backend, server) = Create(new AppSettings
        {
            ModIoAccessToken = "token",
            ModIoGameIds = [6715],
            ModIoPlatform = ""
        });
        server.On("/v1/games/6715/mods/42", Mod(42, "Party Limit"));

        await backend.GetModInfoAsync("42");

        server.Requests.Single().Platform.Should().BeNull();
    }

    [Fact]
    public async Task GetUserModsAsync_WithOnlyAnApiKey_ExplainsThatATokenIsNeeded()
    {
        var (backend, server) = Create(new AppSettings { ModIoApiKey = "secret", ModIoGameIds = [6715] });

        var act = () => backend.GetUserModsAsync();

        (await act.Should().ThrowAsync<ModIoException>()).WithMessage("*access token*");
        server.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUserModsAsync_ReadsEveryPageAndMapsTheMods()
    {
        var (backend, server) = Create();
        server.On("/v1/me/subscribed", query => query.Contains("_offset=0")
            ? Page(total: 2, Mod(1, "First", maturity: 8))
            : Page(total: 2, Mod(2, "Second")));

        var mods = await backend.GetUserModsAsync();

        mods.Select(m => m.Name).Should().Equal("First", "Second");
        mods[0].BackendId.Should().Be(BackendIds.ModIo);
        mods[0].GameDomain.Should().Be("6715");
        mods[0].Author.Should().Be("author");
        mods[0].IsAdult.Should().BeTrue();
        mods[1].IsAdult.Should().BeFalse();
        server.Requests
            .Where(r => r.Uri.AbsolutePath == "/v1/me/subscribed")
            .Should().HaveCount(2).And.AllSatisfy(r => r.Uri.Query.Should().Contain("game_id=6715"));
    }

    [Fact]
    public async Task GetModFilesAsync_MarksTheLiveFileMainAndOlderUploadsOldVersion()
    {
        var (backend, server) = Create();
        server.On("/v1/games/6715/mods/42", Mod(42, "Party Limit", liveFileId: 200));
        server.On("/v1/games/6715/mods/42/files", Page(total: 2, File(200, "v2.zip", "2.0.0"), File(100, "v1.zip", "1.0.0")));

        var all = await backend.GetModFilesAsync("42");
        var main = await backend.GetModFilesAsync("42", filter: FileFilter.MainAndOptional);

        all.Should().HaveCount(2);
        all.Single(f => f.FileId == "200").Category.Should().Be(ModIoBackend.LiveFileCategory);
        all.Single(f => f.FileId == "100").Category.Should().Be(ModIoBackend.OldFileCategory);
        main.Should().ContainSingle().Which.FileName.Should().Be("v2.zip");

        var live = main[0];
        live.Md5.Should().Be("abc123");
        live.SizeBytes.Should().Be(1234);
        live.Version.Should().Be("2.0.0");
        live.DirectDownloadUrl.Should().Be("https://cdn.example/200");
    }

    [Fact]
    public async Task GetModFilesAsync_DropsADownloadLinkThatHasExpired()
    {
        var (backend, server) = Create();
        server.On("/v1/games/6715/mods/42", Mod(42, "Party Limit", liveFileId: 200));
        server.On("/v1/games/6715/mods/42/files", Page(total: 1, File(200, "v2.zip", "2.0.0", expires: 1)));

        var files = await backend.GetModFilesAsync("42");

        files.Single().DirectDownloadUrl.Should().BeNull();
    }

    [Fact]
    public async Task GetModInfoAsync_ForAnUnknownMod_ReturnsNull()
    {
        var (backend, _) = Create();

        var mod = await backend.GetModInfoAsync("999");

        mod.Should().BeNull();
    }

    [Fact]
    public async Task GetModInfoAsync_WithSeveralGames_FindsTheModInTheGameThatHasIt()
    {
        var (backend, server) = Create(new AppSettings { ModIoAccessToken = "token", ModIoGameIds = [1, 6715] });
        server.On("/v1/games/6715/mods/42", Mod(42, "Party Limit"));

        var mod = await backend.GetModInfoAsync("42");

        mod!.GameDomain.Should().Be("6715");
    }

    [Fact]
    public async Task SearchModsAsync_SendsTermsPagingSortAndHidesExplicitMods()
    {
        var (backend, server) = Create();
        server.On("/v1/games/6715/mods", Page(total: 45, Mod(1, "Hit")));

        var result = await backend.SearchModsAsync(new ModSearchQuery
        {
            Terms = "party limit",
            Page = 3,
            PageSize = 20,
            SortBy = ModSortOrder.Downloads
        });

        var query = Uri.UnescapeDataString(server.Requests.Single().Uri.Query);
        query.Should().Contain("_q=party limit");
        query.Should().Contain("_offset=40");
        query.Should().Contain("_sort=-downloads_total");
        query.Should().Contain("maturity_option-lt=8");

        result.TotalCount.Should().Be(45);
        result.Page.Should().Be(3);
        result.HasNextPage.Should().BeFalse();
    }

    [Fact]
    public async Task GetRequirementsAsync_ListsTheModsDependencies()
    {
        var (backend, server) = Create();
        server.On("/v1/games/6715/mods/42/dependencies",
            """{"data":[{"mod_id":7,"name":"Script Extender Helper","name_id":"seh"}],"result_count":1,"result_offset":0,"result_limit":100,"result_total":1}""");

        var requirements = await backend.GetRequirementsAsync(["42"]);

        var requirement = requirements["42"].Should().ContainSingle().Subject;
        requirement.ModId.Should().Be("7");
        requirement.Name.Should().Be("Script Extender Helper");
        requirement.IsExternal.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveDownloadUrlAsync_AsksForAFreshLink()
    {
        var (backend, server) = Create();
        server.On("/v1/games/6715/mods/42/files/200", File(200, "v2.zip", "2.0.0"));

        var url = await backend.ResolveDownloadUrlAsync("42", "200");

        url.Should().Be("https://cdn.example/200");
    }

    [Fact]
    public async Task AFailedRequest_CarriesModIosMessage()
    {
        var (backend, server) = Create();
        server.On("/v1/games/6715/mods", _ => null, HttpStatusCode.Unauthorized,
            """{"error":{"code":401,"error_ref":11005,"message":"The supplied access token has expired."}}""");

        var act = () => backend.SearchModsAsync(new ModSearchQuery { Terms = "x" });

        var thrown = await act.Should().ThrowAsync<ModIoException>();
        thrown.WithMessage("*access token has expired*");
        thrown.Which.ErrorRef.Should().Be(11005);
    }

    [Fact]
    public async Task DownloadModsAsync_AFailedFile_IsReportedAsAFailureAndNotAsDownloaded()
    {
        var (backend, server) = Create();
        server.On("/v1/me/subscribed", Page(total: 1, Mod(42, "Party Limit")));
        // The CDN link is not served, so the transfer fails.

        var reports = new List<DownloadProgress>();
        await backend.DownloadModsAsync(
            Path.Combine(Path.GetTempPath(), "modio-test-" + Guid.NewGuid().ToString("N")),
            options: new DownloadOptions { Filter = FileFilter.MainAndOptional },
            progress: new Modular.Core.Downloads.SyncProgress<DownloadProgress>(reports.Add));

        var failure = reports.Should().ContainSingle(r => r.Phase == DownloadPhase.Failed).Subject;
        failure.CurrentFile.Should().Be("live.zip");
        failure.Error.Should().NotBeNullOrEmpty();
        reports.Should().NotContain(r => r.Status == "Downloaded live.zip");
    }

    // ── games ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchGamesAsync_FindsGamesByNameOnTheConfiguredGamesHost()
    {
        var (backend, server) = Create();
        server.On("/v1/games", Page(2,
            """{"id":5784,"name":"Cyberpunk 2077 Tools","name_id":"cp-tools"}""",
            """{"id":5785,"name":"Cyberpunk 2077","name_id":"cyberpunk2077"}"""));

        var games = await backend.SearchGamesAsync("cyberpunk 2077");

        // The exact name comes first, whatever order mod.io served.
        games.Select(g => g.Id).Should().Equal("5785", "5784");
        games[0].Name.Should().Be("Cyberpunk 2077");

        var request = server.Requests.Should().ContainSingle().Subject;
        request.Uri.Host.Should().Be("g-6715.modapi.io");
        Uri.UnescapeDataString(request.Uri.Query).Should().Contain("_q=cyberpunk 2077");

        // Names learned from a search name the game's folder without another request.
        (await backend.GetGameNameAsync("5785")).Should().Be("Cyberpunk 2077");
        server.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task SearchGamesAsync_WithNoGameConfigured_StillSearches()
    {
        var (backend, server) = Create(new AppSettings { ModIoAccessToken = "token" });
        server.On("/v1/games", Page(1, """{"id":6715,"name":"Baldur's Gate 3","name_id":"baldursgate3"}"""));

        var games = await backend.SearchGamesAsync("baldur");

        games.Should().Equal(new GameMatch("6715", "Baldur's Gate 3"));
        server.Requests.Should().ContainSingle().Which.Uri.Host.Should().Be("g-6715.modapi.io");
    }

    [Fact]
    public async Task SearchGamesAsync_GivenAnId_LooksThatGameUp()
    {
        var (backend, server) = Create();
        server.On("/v1/games/5784", """{"id":5784,"name":"Cyberpunk 2077","name_id":"cyberpunk2077"}""");

        (await backend.SearchGamesAsync("5784")).Should().Equal(new GameMatch("5784", "Cyberpunk 2077"));
        (await backend.SearchGamesAsync("999")).Should().BeEmpty();
    }

    // ── version provider ────────────────────────────────────────────────────

    [Theory]
    [InlineData("modio:6715:42", "6715", "42")]
    [InlineData("modio:42", null, "42")]
    [InlineData("42", null, "42")]
    public void ParseCanonicalId_AcceptsEveryForm(string canonical, string? game, string mod)
    {
        ModIoVersionProvider.ParseCanonicalId(canonical).Should().Be((game, mod));
    }

    // ── subscriptions ───────────────────────────────────────────────────────

    private const string SubscribePath = "/v1/games/6715/mods/42/subscribe";

    [Theory]
    [InlineData(HttpStatusCode.Created, TrackOutcome.Tracked)]
    [InlineData(HttpStatusCode.OK, TrackOutcome.AlreadyTracked)]
    public async Task TrackAsync_Subscribes(HttpStatusCode status, TrackOutcome expected)
    {
        var (backend, server) = Create();
        server.OnWrite(HttpMethod.Post, SubscribePath, status, Mod(42, "Alfira"));

        var result = await backend.TrackAsync("42");

        result.Outcome.Should().Be(expected);
        result.Mod!.Name.Should().Be("Alfira");
        var request = server.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Authorization.Should().Be("Bearer token");
        request.ContentType.Should().Be("application/x-www-form-urlencoded");
        request.Uri.Query.Should().NotContain("api_key");
    }

    [Fact]
    public async Task UntrackAsync_AcceptsEmpty204()
    {
        var (backend, server) = Create();
        server.OnWrite(HttpMethod.Delete, SubscribePath, HttpStatusCode.NoContent);

        var result = await backend.UntrackAsync("42");

        result.Outcome.Should().Be(TrackOutcome.Untracked);
        server.Requests.Should().ContainSingle()
            .Which.ContentType.Should().Be("application/x-www-form-urlencoded");
    }

    [Fact]
    public async Task UntrackAsync_NotSubscribed_IsNotAnError()
    {
        var (backend, server) = Create();
        server.OnWrite(HttpMethod.Delete, SubscribePath, HttpStatusCode.BadRequest,
            """{"error":{"code":400,"error_ref":15005,"message":"You are not subscribed to this mod."}}""");

        var result = await backend.UntrackAsync("42");

        result.Outcome.Should().Be(TrackOutcome.NotTracked);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 11003, TrackingFailure.MissingWriteScope)]
    [InlineData(HttpStatusCode.Unauthorized, 11005, TrackingFailure.NotAuthenticated)]
    [InlineData(HttpStatusCode.Forbidden, 15001, TrackingFailure.Unavailable)]
    [InlineData(HttpStatusCode.Forbidden, 15000, TrackingFailure.Unavailable)]
    [InlineData(HttpStatusCode.Forbidden, 11006, TrackingFailure.Unavailable)]
    public async Task TrackAsync_MapsRefusals(HttpStatusCode status, int errorRef, TrackingFailure expected)
    {
        var (backend, server) = Create();
        server.OnWrite(HttpMethod.Post, SubscribePath, status,
            $$$"""{"error":{"code":{{{(int)status}}},"error_ref":{{{errorRef}}},"message":"refused"}}""");

        var act = () => backend.TrackAsync("42");

        (await act.Should().ThrowAsync<TrackingException>()).Which.Reason.Should().Be(expected);
    }

    [Fact]
    public async Task TrackAsync_WithOnlyApiKey_SendsNothing()
    {
        var (backend, server) = Create(new AppSettings { ModIoApiKey = "key", ModIoGameIds = [(int)Bg3] });

        var act = () => backend.TrackAsync("42");

        (await act.Should().ThrowAsync<TrackingException>()).Which.Reason.Should().Be(TrackingFailure.NotAuthenticated);
        server.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task TrackAsync_RejectsNonNumericId()
    {
        var (backend, _) = Create();

        var act = () => backend.TrackAsync("alfira");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetTrackedIdsAsync_ListsSubscriptions()
    {
        var (backend, server) = Create();
        server.On("/v1/me/subscribed", Page(2, Mod(42, "Alfira"), Mod(7, "Other")));

        var ids = await backend.GetTrackedIdsAsync();

        ids.Should().BeEquivalentTo(["42", "7"]);
        backend.Capabilities.Should().HaveFlag(BackendCapabilities.Tracking);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static (ModIoBackend Backend, FakeModIo Server) Create(AppSettings? settings = null)
    {
        settings ??= new AppSettings { ModIoAccessToken = "token", ModIoGameIds = [(int)Bg3] };
        var server = new FakeModIo();
        var client = new ModIoClient(
            () => settings.ModIoApiKey,
            () => settings.ModIoAccessToken,
            () => settings.ModIoPlatform,
            server,
            throttleDelay: TimeSpan.Zero);

        var downloader = new Modular.Core.Downloads.ResumableDownloader(server);
        return (new ModIoBackend(settings, downloader: downloader, client: client), server);
    }

    private static string Mod(long id, string name, int maturity = 0, long liveFileId = 200) => $$"""
        {
          "id": {{id}}, "game_id": {{Bg3}}, "name": "{{name}}", "name_id": "mod-{{id}}",
          "summary": "s", "profile_url": "https://mod.io/g/baldursgate3/m/mod-{{id}}",
          "date_updated": 1700000000, "maturity_option": {{maturity}},
          "submitted_by": { "id": 1, "username": "author" },
          "logo": { "original": "https://img/{{id}}.png", "thumb_320x180": "https://img/{{id}}-thumb.png" },
          "stats": { "downloads_total": 10 },
          "modfile": {{File(liveFileId, "live.zip", "2.0.0")}}
        }
        """;

    private static string File(long id, string name, string version, long expires = 0) => $$"""
        {
          "id": {{id}}, "mod_id": 42, "date_added": 1700000000, "filesize": 1234,
          "filehash": { "md5": "abc123" }, "filename": "{{name}}", "version": "{{version}}",
          "download": { "binary_url": "https://cdn.example/{{id}}", "date_expires": {{expires}} }
        }
        """;

    private static string Page(int total, params string[] items) =>
        $$"""{"data":[{{string.Join(",", items)}}],"result_count":{{items.Length}},"result_offset":0,"result_limit":100,"result_total":{{total}}}""";

    private sealed record SeenRequest(
        Uri Uri, string? Authorization, string? Platform, HttpMethod? Method = null, string? ContentType = null, string? Body = null);

    /// <summary>Serves canned responses by path; anything else is a mod.io 404.</summary>
    private sealed class FakeModIo : HttpMessageHandler
    {
        private readonly Dictionary<string, (Func<string, string?> Body, HttpStatusCode Status, string? Error)> _routes = new();

        public List<SeenRequest> Requests { get; } = new();

        public void On(string path, string body) => On(path, _ => body);

        public void On(
            string path,
            Func<string, string?> body,
            HttpStatusCode status = HttpStatusCode.OK,
            string? error = null) =>
            _routes[path] = (body, status, error);

        private readonly Dictionary<(HttpMethod, string), (HttpStatusCode Status, string Body)> _writes = new();

        public void OnWrite(HttpMethod method, string path, HttpStatusCode status, string body = "") =>
            _writes[(method, path)] = (status, body);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new SeenRequest(
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("X-Modio-Platform", out var platform) ? platform.Single() : null,
                request.Method,
                request.Content?.Headers.ContentType?.MediaType,
                request.Content == null ? null : await request.Content.ReadAsStringAsync(ct)));

            if (_writes.TryGetValue((request.Method, request.RequestUri!.AbsolutePath), out var write))
            {
                return new HttpResponseMessage(write.Status)
                {
                    Content = new StringContent(write.Body, Encoding.UTF8, "application/json")
                };
            }

            var (status, content) = _routes.TryGetValue(request.RequestUri!.AbsolutePath, out var route)
                ? (route.Status, route.Error ?? route.Body(Uri.UnescapeDataString(request.RequestUri.Query)))
                : (HttpStatusCode.NotFound, """{"error":{"code":404,"error_ref":15022,"message":"The requested mod could not be found."}}""");

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(content ?? "{}", Encoding.UTF8, "application/json")
            };
        }
    }
}
