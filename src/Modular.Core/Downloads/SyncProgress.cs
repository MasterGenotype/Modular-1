namespace Modular.Core.Downloads;

/// <summary>
/// <see cref="IProgress{T}"/> implementation that invokes its callback synchronously on the
/// reporting thread. Unlike <see cref="Progress{T}"/>, reports are never reordered or posted
/// to a synchronization context.
/// </summary>
public sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;

    public SyncProgress(Action<T> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public void Report(T value) => _handler(value);
}
