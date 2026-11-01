using System.Net;
using System.Text;
using System.Web;
using Modular.Core.Backends.NexusMods;
using Modular.Core.Configuration;
using Modular.Core.Database;
using Modular.Core.RateLimiting;
using Modular.Sdk.Backends;
using Xunit;

namespace Modular.Core.Tests.Backends;

public sealed class NexusTrackingTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "modular-nexus-tracking-tests", Guid.NewGuid().ToString("N"));

    public NexusTrackingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(201, TrackOutcome.Tracked)]
    [InlineData(200, TrackOutcome.AlreadyTracked)]
    public async Task TrackAsync_MapsStatus(int status, TrackOutcome expected)
    {
        var server = new FakeNexus { Status = (HttpStatusCode)status };
        var backend = CreateBackend(server);

        var result = await backend.TrackAsync("1933", "baldursgate3");

        Assert.Equal(expected, result.Outcome);
        var request = Assert.Single(server.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/user/tracked_mods.json", request.Path);
        Assert.Equal("baldursgate3", request.Query["domain_name"]);
        Assert.Equal("mod_id=1933", request.Body);
        Assert.Equal("the-key", request.ApiKey);
    }

    [Theory]
    [InlineData(200, TrackOutcome.Untracked)]
    [InlineData(404, TrackOutcome.NotTracked)]
    public async Task UntrackAsync_MapsStatusAndSendsFormOnDelete(int status, TrackOutcome expected)
    {
        var server = new FakeNexus { Status = (HttpStatusCode)status };
        var backend = CreateBackend(server);

        var result = await backend.UntrackAsync("1933", "baldursgate3");

        Assert.Equal(expected, result.Outcome);
        var request = Assert.Single(server.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("mod_id=1933", request.Body);
    }

    [Theory]
    [InlineData(401, TrackingFailure.NotAuthenticated)]
    [InlineData(429, TrackingFailure.RateLimited)]
    [InlineData(500, TrackingFailure.Other)]
    public async Task TrackAsync_ReportsRefusal(int status, TrackingFailure expected)
    {
        var backend = CreateBackend(new FakeNexus { Status = (HttpStatusCode)status });

        var ex = await Assert.ThrowsAsync<TrackingException>(() => backend.TrackAsync("1933", "baldursgate3"));

        Assert.Equal(expected, ex.Reason);
    }

    [Fact]
    public async Task TrackAsync_WithoutKey_SendsNothing()
    {
        var server = new FakeNexus();
        var backend = CreateBackend(server, apiKey: "");

        var ex = await Assert.ThrowsAsync<TrackingException>(() => backend.TrackAsync("1933", "baldursgate3"));

        Assert.Equal(TrackingFailure.NotAuthenticated, ex.Reason);
        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-4")]
    public async Task TrackAsync_RejectsNonNumericId(string modId)
    {
        var backend = CreateBackend(new FakeNexus());

        await Assert.ThrowsAsync<ArgumentException>(() => backend.TrackAsync(modId, "baldursgate3"));
    }

    [Fact]
    public async Task TrackAsync_RequiresGameDomain()
    {
        var backend = CreateBackend(new FakeNexus());

        await Assert.ThrowsAsync<ArgumentException>(() => backend.TrackAsync("1933"));
    }

    [Fact]
    public async Task ChangesUpdateTrackedCache()
    {
        var server = new FakeNexus
        {
            TrackedList = """[{"mod_id":141,"domain_name":"baldursgate3"},{"mod_id":7,"domain_name":"skyrim"}]"""
        };
        var backend = CreateBackend(server);

        Assert.Equal(["141"], await backend.GetTrackedIdsAsync("baldursgate3"));

        server.Status = HttpStatusCode.Created;
        await backend.TrackAsync("1933", "baldursgate3");
        Assert.True(await backend.IsModTrackedAsync("baldursgate3", 1933));

        server.Status = HttpStatusCode.OK;
        await backend.UntrackAsync("141", "baldursgate3");
        Assert.False(await backend.IsModTrackedAsync("baldursgate3", 141));
        Assert.True(await backend.IsModTrackedAsync("skyrim", 7));
    }

    private NexusModsBackend CreateBackend(FakeNexus server, string apiKey = "the-key") =>
        new(
            new AppSettings { NexusApiKey = apiKey },
            new NexusRateLimiter(),
            new DownloadDatabase(Path.Combine(_dir, "downloads.json")),
            new ModMetadataCache(Path.Combine(_dir, "cache.json")),
            logger: null,
            downloader: null,
            apiHandler: server);

    private sealed record SeenRequest(
        HttpMethod Method, string Path, System.Collections.Specialized.NameValueCollection Query, string Body, string? ApiKey);

    /// <summary>The NexusMods tracked-mods endpoint, answering writes with <see cref="Status"/>.</summary>
    private sealed class FakeNexus : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public string TrackedList { get; init; } = "[]";

        public List<SeenRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (request.Method == HttpMethod.Get && uri.AbsolutePath == "/v1/user/tracked_mods.json")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(TrackedList, Encoding.UTF8, "application/json")
                };
            }

            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new SeenRequest(
                request.Method,
                uri.AbsolutePath,
                HttpUtility.ParseQueryString(uri.Query),
                body,
                request.Headers.TryGetValues("apikey", out var keys) ? keys.Single() : null));

            return new HttpResponseMessage(Status)
            {
                Content = new StringContent("""{"message":"fake"}""", Encoding.UTF8, "application/json")
            };
        }
    }
}
