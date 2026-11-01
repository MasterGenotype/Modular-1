using FluentAssertions;
using Modular.Core.Services;
using Modular.Sdk.Backends;
using Modular.Sdk.Backends.Common;
using Xunit;

namespace Modular.Core.Tests.Services;

public sealed class ModTrackingServiceTests
{
    [Theory]
    [InlineData("1933", null, null, "1933", false)]
    [InlineData("https://www.nexusmods.com/baldursgate3/mods/1933", "nexusmods", "baldursgate3", "1933", false)]
    [InlineData("nexusmods.com/baldursgate3/mods/1933?tab=files", "nexusmods", "baldursgate3", "1933", false)]
    [InlineData("https://mod.io/g/baldursgate3/m/alfira-joins-the-party", "modio", null, "alfira-joins-the-party", true)]
    public void Parse_ReadsIdsAndLinks(string input, string? backend, string? game, string id, bool pageName)
    {
        ModTrackingService.Parse(input).Should().Be(new ModReference(backend, game, id, pageName));
    }

    [Theory]
    [InlineData("alfira")]
    [InlineData("https://example.com/mods/1")]
    [InlineData("")]
    public void Parse_RejectsAnythingElse(string input)
    {
        var act = () => ModTrackingService.Parse(input);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task ApplyAsync_CarriesOnPastAFailure_ButStopsWhenCredentialsFail()
    {
        var backend = new FakeTracking(id => id switch
        {
            "2" => throw new TrackingException(TrackingFailure.ModNotFound, "no such mod"),
            "3" => throw new TrackingException(TrackingFailure.MissingWriteScope, "read-only token"),
            _ => new TrackResult(TrackOutcome.Tracked)
        });

        var results = await new ModTrackingService().ApplyAsync(backend, ["1", "2", "3", "4"], "g", track: true);

        results.Select(r => r.Result?.Outcome.ToString() ?? r.Failure.ToString())
            .Should().Equal("Tracked", "ModNotFound", "MissingWriteScope", "NotAuthenticated");
        backend.Calls.Should().Equal("1", "2", "3");
    }

    [Fact]
    public async Task ApplyAsync_TakesTheGameFromANexusLink()
    {
        var backend = new FakeTracking(_ => new TrackResult(TrackOutcome.Tracked));

        await new ModTrackingService().ApplyAsync(
            backend, ["https://www.nexusmods.com/baldursgate3/mods/141"], gameDomain: null, track: true);

        backend.Games.Should().Equal("baldursgate3");
    }

    [Fact]
    public async Task ApplyAsync_SkipsWhatBeforeRefuses()
    {
        var backend = new FakeTracking(_ => new TrackResult(TrackOutcome.Tracked));

        var results = await new ModTrackingService().ApplyAsync(
            backend, ["1", "2"], "g", track: true, before: (id, _) => Task.FromResult(id != "1"));

        results[0].Error.Should().Be("skipped");
        backend.Calls.Should().Equal("2");
    }

    [Fact]
    public async Task ApplyAsync_RefusesALinkForAnotherBackend()
    {
        var backend = new FakeTracking(_ => new TrackResult(TrackOutcome.Tracked));

        var results = await new ModTrackingService().ApplyAsync(
            backend, ["https://mod.io/g/baldursgate3/m/alfira"], "g", track: true);

        results.Single().Failure.Should().Be(TrackingFailure.ModNotFound);
        backend.Calls.Should().BeEmpty();
    }

    private sealed class FakeTracking(Func<string, TrackResult> answer) : IModBackend, ITrackingBackend
    {
        public List<string> Calls { get; } = [];
        public List<string?> Games { get; } = [];
        public string Id => BackendIds.NexusMods;
        public string DisplayName => "Fake";
        public BackendCapabilities Capabilities => BackendCapabilities.Tracking;
        public TrackingTerms Terms => TrackingTerms.Track;

        public Task<TrackResult> TrackAsync(string modId, string? gameDomain = null, CancellationToken ct = default)
        {
            Calls.Add(modId);
            Games.Add(gameDomain);
            return Task.FromResult(answer(modId));
        }

        public Task<TrackResult> UntrackAsync(string modId, string? gameDomain = null, CancellationToken ct = default) =>
            TrackAsync(modId, gameDomain, ct);

        public Task<IReadOnlySet<string>> GetTrackedIdsAsync(string? gameDomain = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

        public IReadOnlyList<string> ValidateConfiguration() => [];
        public Task<List<BackendMod>> GetUserModsAsync(string? gameDomain = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<BackendModFile>> GetModFilesAsync(string modId, string? gameDomain = null, FileFilter? filter = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> ResolveDownloadUrlAsync(string modId, string fileId, string? gameDomain = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DownloadModsAsync(string outputDirectory, string? gameDomain = null, DownloadOptions? options = null, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BackendMod?> GetModInfoAsync(string modId, string? gameDomain = null, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
