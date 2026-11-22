namespace Modular.Gui.Services;

/// <summary>
/// One game-changing operation at a time across all games (installing updates, applying a mod
/// list, switching a build): each deploys into a game folder and writes Modular's database, and
/// two of them at once would race. Checks and lookups don't take it.
/// </summary>
public static class InstallGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>What holds the gate now, for the "waiting" message.</summary>
    public static string? Holder { get; private set; }

    /// <summary>Waits for the gate (reporting what it waits on), then holds it until disposed.</summary>
    public static async Task<IDisposable> EnterAsync(string what, Action<string>? waiting = null, CancellationToken ct = default)
    {
        if (!Gate.Wait(0))
        {
            waiting?.Invoke($"Waiting for {Holder ?? "another install"} to finish…");
            await Gate.WaitAsync(ct);
        }
        Holder = what;
        return new Release();
    }

    private sealed class Release : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                Holder = null;
                Gate.Release();
            }
        }
    }
}
