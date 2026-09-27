using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using FluentAssertions;
using Modular.Core.Downloads;
using Xunit;

namespace Modular.Core.Tests.Downloads;

public sealed class ResumableDownloaderTests : IDisposable
{
    private const string Url = "https://cdn.example.com/files/mod.zip";
    private const string Identity = "test:mod:1";

    private readonly string _dir;
    private readonly string _dest;
    private readonly byte[] _data;
    private readonly FakeRangeServer _server;

    private static readonly ResumableDownloaderOptions FastOptions = new()
    {
        InitialRetryDelay = TimeSpan.FromMilliseconds(1),
        MaxRetryDelay = TimeSpan.FromMilliseconds(5),
        StallTimeout = TimeSpan.FromSeconds(10)
    };

    public ResumableDownloaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "modular-resumable-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dest = Path.Combine(_dir, "mod.zip");
        _data = RandomBytes(100_000, seed: 42);
        _server = new FakeRangeServer(_data, "\"v1\"");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private ResumableDownloader CreateDownloader(ResumableDownloaderOptions? options = null) =>
        new(_server, options ?? FastOptions);

    private ResumableDownloadRequest Request(
        string? identity = null,
        int maxAttempts = 5,
        string? md5 = null,
        bool verifyMd5 = false,
        long? expectedSize = null,
        bool resume = true,
        string url = Url,
        Func<CancellationToken, Task<string?>>? refresh = null) => new()
    {
        Url = url,
        DestinationPath = _dest,
        Identity = identity ?? Identity,
        MaxAttempts = maxAttempts,
        ExpectedMd5 = md5,
        VerifyMd5 = verifyMd5,
        ExpectedSize = expectedSize,
        Resume = resume,
        RefreshUrlAsync = refresh
    };

    private string PartPath => ResumableDownloader.GetPartPath(_dest);
    private string StatePath => ResumableDownloader.GetStatePath(_dest);

    /// <summary>Leaves a partial download of <paramref name="bytes"/> bytes on disk.</summary>
    private async Task CreatePartialAsync(int bytes, string? identity = null, string? md5 = null)
    {
        _server.FailNextAfter(bytes);
        using var downloader = CreateDownloader();
        var act = () => downloader.DownloadAsync(Request(identity: identity, maxAttempts: 1, md5: md5));
        await act.Should().ThrowAsync<IOException>();
        new FileInfo(PartPath).Length.Should().Be(bytes);
        File.Exists(StatePath).Should().BeTrue();
        _server.Requests.Clear();
    }

    [Fact]
    public async Task FreshDownload_WritesFileAndCleansUp()
    {
        using var downloader = CreateDownloader();

        var result = await downloader.DownloadAsync(Request());

        File.ReadAllBytes(_dest).Should().Equal(_data);
        File.Exists(PartPath).Should().BeFalse();
        File.Exists(StatePath).Should().BeFalse();
        result.Resumed.Should().BeFalse();
        result.FileSize.Should().Be(_data.Length);
        result.BytesTransferred.Should().Be(_data.Length);
        _server.Requests.Should().ContainSingle().Which.RangeFrom.Should().BeNull();
    }

    [Fact]
    public async Task InterruptedTransfer_RetriesWithRangeAndIfRange()
    {
        _server.FailNextAfter(30_000);
        using var downloader = CreateDownloader();

        var result = await downloader.DownloadAsync(Request());

        File.ReadAllBytes(_dest).Should().Equal(_data);
        result.Resumed.Should().BeTrue();
        result.Attempts.Should().Be(2);
        result.BytesTransferred.Should().Be(_data.Length);
        _server.Requests.Should().HaveCount(2);
        _server.Requests[1].RangeFrom.Should().Be(30_000);
        _server.Requests[1].IfRange.Should().Be("\"v1\"");
    }

    [Fact]
    public async Task PartialFromPreviousRun_IsResumed()
    {
        await CreatePartialAsync(40_000);
        using var downloader = CreateDownloader();

        var result = await downloader.DownloadAsync(Request());

        File.ReadAllBytes(_dest).Should().Equal(_data);
        result.Resumed.Should().BeTrue();
        result.BytesTransferred.Should().Be(_data.Length - 40_000);
        _server.Requests.Should().ContainSingle().Which.RangeFrom.Should().Be(40_000);
        File.Exists(PartPath).Should().BeFalse();
        File.Exists(StatePath).Should().BeFalse();
    }

    [Fact]
    public async Task ChangedRemoteFile_StaleValidator_RestartsFromZero()
    {
        await CreatePartialAsync(40_000);
        var newData = RandomBytes(120_000, seed: 7);
        _server.SetContent(newData, "\"v2\"");
        using var downloader = CreateDownloader();

        var result = await downloader.DownloadAsync(Request());

        File.ReadAllBytes(_dest).Should().Equal(newData);
        result.Resumed.Should().BeFalse();
        _server.Requests.Should().ContainSingle();
        _server.Requests[0].RangeFrom.Should().Be(40_000);
        _server.Requests[0].Status.Should().Be(200);
    }

    [Fact]
    public async Task RangeAtEndOfFile_416_FinalizesWithoutDownloading()
    {
        // All bytes arrive, then the connection drops before completion is observed.
        await CreatePartialAsync(_data.Length);
        using var downloader = CreateDownloader();

        var result = await downloader.DownloadAsync(Request());

        File.ReadAllBytes(_dest).Should().Equal(_data);
        result.BytesTransferred.Should().Be(0);
        _server.Requests.Should().ContainSingle().Which.Status.Should().Be(416);
    }

    [Fact]
    public async Task MismatchedContentRange_RestartsFromZero()
    {
        await CreatePartialAsync(40_000);
        _server.Hook = req =>
        {
            if (req.Headers.Range == null)
                return null;
            // Misbehaving server: claims a partial response starting at byte 0.
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(_data[..1000])
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 999, _data.Length);
            return response;
        };
        using var downloader = CreateDownloader();

        var result = await downloader.DownloadAsync(Request());

        File.ReadAllBytes(_dest).Should().Equal(_data);
        _server.Requests.Should().HaveCount(2);
        _server.Requests[1].RangeFrom.Should().BeNull();
    }

    [Fact]
    public async Task DifferentIdentity_DiscardsPartial()
    {
        await CreatePartialAsync(40_000, identity: "test:other:9");
        using var downloader = CreateDownloader();

        var result = await downloader.DownloadAsync(Request());

        File.ReadAllBytes(_dest).Should().Equal(_data);
        result.Resumed.Should().BeFalse();
        _server.Requests.Should().ContainSingle().Which.RangeFrom.Should().BeNull();
    }

    [Fact]
    public async Task ResumeDisabled_DiscardsPartial()
    {
        await CreatePartialAsync(40_000);
        using var downloader = CreateDownloader();

        var result = await downloader.DownloadAsync(Request(resume: false));

        File.ReadAllBytes(_dest).Should().Equal(_data);
        result.Resumed.Should().BeFalse();
        _server.Requests.Should().ContainSingle().Which.RangeFrom.Should().BeNull();
    }

    [Fact]
    public async Task Cancellation_KeepsPartialDataAndSidecar()
    {
        _server.StallNextAfter(25_000);
        using var downloader = CreateDownloader();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var act = () => downloader.DownloadAsync(Request(), ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(_dest).Should().BeFalse();
        new FileInfo(PartPath).Length.Should().Be(25_000);
        File.Exists(StatePath).Should().BeTrue();

        // And it resumes afterwards.
        _server.Requests.Clear();
        var result = await downloader.DownloadAsync(Request());
        File.ReadAllBytes(_dest).Should().Equal(_data);
        result.Resumed.Should().BeTrue();
        _server.Requests.Should().ContainSingle().Which.RangeFrom.Should().Be(25_000);
    }

    [Fact]
    public async Task StalledTransfer_TimesOutAndResumes()
    {
        _server.StallNextAfter(25_000);
        using var downloader = CreateDownloader(new ResumableDownloaderOptions
        {
            InitialRetryDelay = TimeSpan.FromMilliseconds(1),
            StallTimeout = TimeSpan.FromMilliseconds(300)
        });

        var result = await downloader.DownloadAsync(Request());

        File.ReadAllBytes(_dest).Should().Equal(_data);
        result.Resumed.Should().BeTrue();
        _server.Requests.Should().HaveCount(2);
        _server.Requests[1].RangeFrom.Should().Be(25_000);
    }

    [Fact]
    public async Task Md5Mismatch_DeletesDataAndThrows()
    {
        using var downloader = CreateDownloader();

        var act = () => downloader.DownloadAsync(Request(md5: new string('0', 32), verifyMd5: true));

        await act.Should().ThrowAsync<DownloadIntegrityException>().WithMessage("*MD5 mismatch*");
        File.Exists(_dest).Should().BeFalse();
        File.Exists(PartPath).Should().BeFalse();
        File.Exists(StatePath).Should().BeFalse();
    }

    [Fact]
    public async Task Md5Match_IsReportedAsVerified()
    {
        using var downloader = CreateDownloader();

        var result = await downloader.DownloadAsync(Request(md5: Md5(_data).ToUpperInvariant(), verifyMd5: true));

        result.Md5Verified.Should().BeTrue();
        result.Md5.Should().Be(Md5(_data));
    }

    [Fact]
    public async Task ResumedDownload_IsAlwaysVerified_AndCorruptSpliceRestartsFromZero()
    {
        var newData = RandomBytes(_data.Length, seed: 99);
        await CreatePartialAsync(40_000, md5: Md5(newData));
        // Misbehaving server: content changed but the ETag did not, so the splice is corrupt.
        _server.SetContent(newData, "\"v1\"");
        using var downloader = CreateDownloader();

        // VerifyMd5 is false, but a resumed download is verified anyway.
        var result = await downloader.DownloadAsync(Request(md5: Md5(newData)));

        File.ReadAllBytes(_dest).Should().Equal(newData);
        result.Resumed.Should().BeFalse();
        _server.Requests.Should().HaveCount(2);
        _server.Requests[0].RangeFrom.Should().Be(40_000);
        _server.Requests[1].RangeFrom.Should().BeNull();
    }

    [Fact]
    public async Task RestartAfterResume_IsNotReportedAsResumed()
    {
        var md5 = Md5(_data);
        await CreatePartialAsync(40_000, md5: md5);
        // The first attempt resumes with 206 and then dies mid-stream.
        _server.FailNextAfter(10_000);
        // Before the second attempt the validator changes, so the server replies 200
        // and the whole file is rewritten from zero.
        _server.Hook = _ =>
        {
            if (_server.Requests.Count == 1)
                _server.SetContent(_data, "\"v2\"");
            return null;
        };
        using var downloader = CreateDownloader();

        var result = await downloader.DownloadAsync(Request(md5: md5));

        File.ReadAllBytes(_dest).Should().Equal(_data);
        // No previously downloaded bytes survived, so this is not a splice: the MD5 is
        // not force-verified and the result must not claim the download resumed.
        result.Resumed.Should().BeFalse();
        result.Md5.Should().BeNull();
        result.Attempts.Should().Be(2);
        _server.Requests[0].Status.Should().Be(206);
        _server.Requests[1].RangeFrom.Should().Be(50_000);
        _server.Requests[1].Status.Should().Be(200);
    }

    [Fact]
    public async Task SizeMismatch_Throws()
    {
        using var downloader = CreateDownloader();

        var act = () => downloader.DownloadAsync(Request(expectedSize: _data.Length + 1));

        await act.Should().ThrowAsync<DownloadIntegrityException>().WithMessage("*Size mismatch*");
        File.Exists(_dest).Should().BeFalse();
        File.Exists(PartPath).Should().BeFalse();
    }

    [Fact]
    public async Task RejectedUrl_IsRefreshed()
    {
        const string expiredUrl = "https://cdn.example.com/files/mod.zip?expires=1";
        _server.Hook = req => req.RequestUri!.ToString() == expiredUrl
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : null;
        var refreshCalls = 0;
        using var downloader = CreateDownloader();

        var result = await downloader.DownloadAsync(Request(url: expiredUrl, refresh: _ =>
        {
            refreshCalls++;
            return Task.FromResult<string?>(Url);
        }));

        File.ReadAllBytes(_dest).Should().Equal(_data);
        refreshCalls.Should().Be(1);
        result.FinalUrl.Should().Be(Url);
        _server.Requests.Select(r => r.Url).Should().Equal(expiredUrl, Url);
    }

    [Fact]
    public async Task RejectedUrl_WithoutRefresh_Throws()
    {
        _server.Hook = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);
        using var downloader = CreateDownloader();

        var act = () => downloader.DownloadAsync(Request());

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _server.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ServerWithoutRangeSupport_RestartsFromZero()
    {
        _server.SupportsRanges = false;
        _server.FailNextAfter(30_000);
        using var downloader = CreateDownloader();

        var result = await downloader.DownloadAsync(Request());

        File.ReadAllBytes(_dest).Should().Equal(_data);
        result.Resumed.Should().BeFalse();
        _server.Requests.Should().HaveCount(2);
        _server.Requests[1].Status.Should().Be(200);
    }

    [Fact]
    public async Task ServerError_IsRetried()
    {
        var failures = 0;
        _server.Hook = _ => failures++ < 2 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null;
        using var downloader = CreateDownloader();

        var result = await downloader.DownloadAsync(Request());

        File.ReadAllBytes(_dest).Should().Equal(_data);
        result.Attempts.Should().Be(3);
    }

    private static byte[] RandomBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static string Md5(byte[] data) => Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();

    /// <summary>Snapshot of a request received by the fake server.</summary>
    private sealed record RecordedRequest(string Url, long? RangeFrom, string? IfRange, int Status);

    /// <summary>
    /// In-memory HTTP server that honours Range / If-Range and can inject failures.
    /// </summary>
    private sealed class FakeRangeServer : HttpMessageHandler
    {
        private readonly ConcurrentQueue<(int bytes, bool stall)> _faults = new();
        private byte[] _content;
        private string _etag;

        public FakeRangeServer(byte[] content, string etag)
        {
            _content = content;
            _etag = etag;
        }

        public bool SupportsRanges { get; set; } = true;
        public List<RecordedRequest> Requests { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage?>? Hook { get; set; }

        public void SetContent(byte[] content, string etag)
        {
            _content = content;
            _etag = etag;
        }

        /// <summary>The next response body throws an IOException after this many bytes.</summary>
        public void FailNextAfter(int bytes) => _faults.Enqueue((bytes, false));

        /// <summary>The next response body hangs (until cancelled) after this many bytes.</summary>
        public void StallNextAfter(int bytes) => _faults.Enqueue((bytes, true));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = Hook?.Invoke(request) ?? Serve(request);
            response.RequestMessage = request;
            Requests.Add(new RecordedRequest(
                request.RequestUri!.ToString(),
                request.Headers.Range?.Ranges.FirstOrDefault()?.From,
                request.Headers.IfRange?.ToString(),
                (int)response.StatusCode));
            return Task.FromResult(response);
        }

        private HttpResponseMessage Serve(HttpRequestMessage request)
        {
            var data = _content;
            var from = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
            var honourRange = SupportsRanges && from != null;

            var ifRange = request.Headers.IfRange;
            if (honourRange && ifRange?.EntityTag != null && ifRange.EntityTag.Tag != _etag)
                honourRange = false;

            HttpResponseMessage response;
            byte[] body;
            if (honourRange && from >= data.Length)
            {
                response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    Content = new ByteArrayContent([])
                };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(data.Length);
                return response;
            }

            if (honourRange)
            {
                body = data[(int)from!.Value..];
                response = new HttpResponseMessage(HttpStatusCode.PartialContent);
            }
            else
            {
                body = data;
                response = new HttpResponseMessage(HttpStatusCode.OK);
            }

            Stream stream = new MemoryStream(body);
            if (_faults.TryDequeue(out var fault))
                stream = new FaultyStream(stream, fault.bytes, fault.stall);

            response.Content = new StreamContent(stream);
            response.Content.Headers.ContentLength = body.Length;
            if (honourRange)
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from!.Value, data.Length - 1, data.Length);
            response.Headers.ETag = new EntityTagHeaderValue(_etag);
            response.Headers.AcceptRanges.Add("bytes");
            return response;
        }
    }

    /// <summary>Stream that fails or hangs after delivering a fixed number of bytes.</summary>
    private sealed class FaultyStream : Stream
    {
        private readonly Stream _inner;
        private readonly bool _stall;
        private long _remaining;

        public FaultyStream(Stream inner, long failAfter, bool stall)
        {
            _inner = inner;
            _remaining = failAfter;
            _stall = stall;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0)
            {
                if (_stall)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                throw new IOException("Simulated connection reset");
            }

            var toRead = (int)Math.Min(buffer.Length, _remaining);
            var read = await _inner.ReadAsync(buffer[..toRead], cancellationToken);
            _remaining -= read;
            return read;
        }
    }
}
