using Modular.Core.Backends.NexusMods;
using Xunit;

namespace Modular.Core.Tests.Backends;

public sealed class NxmLinkQueueTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "modular-nxm-queue-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void TakeAll_ReturnsLinksOldestFirstAndEmptiesQueue()
    {
        var queue = new NxmLinkQueue(_dir);
        queue.Add("nxm://g/mods/1/files/1");
        queue.Add("nxm://g/mods/2/files/2");
        queue.Add("nxm://g/mods/3/files/3");

        Assert.True(queue.HasPending);
        Assert.Equal(
            ["nxm://g/mods/1/files/1", "nxm://g/mods/2/files/2", "nxm://g/mods/3/files/3"],
            queue.TakeAll());
        Assert.False(queue.HasPending);
        Assert.Empty(queue.TakeAll());
    }

    [Fact]
    public void TryBecomeWorker_AllowsOneWorkerAtATime()
    {
        var first = new NxmLinkQueue(_dir);
        var second = new NxmLinkQueue(_dir);

        using (var worker = first.TryBecomeWorker())
        {
            Assert.NotNull(worker);
            Assert.Null(second.TryBecomeWorker());
        }

        using var next = second.TryBecomeWorker();
        Assert.NotNull(next);
    }

    [Fact]
    public void HasPending_IsFalseBeforeAnythingIsQueued()
    {
        Assert.False(new NxmLinkQueue(_dir).HasPending);
    }
}
