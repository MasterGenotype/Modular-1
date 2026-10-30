namespace Modular.Core.Backends.NexusMods;

/// <summary>
/// A folder of <c>nxm://</c> links waiting to be downloaded, shared by every Modular
/// process. A browser starts one handler per click; the first one becomes the worker
/// and downloads the links (several at once), and the rest add their link and exit.
/// </summary>
/// <remarks>
/// A handler adds its link, then tries to become the worker. A worker releases the
/// lock, then looks for links again. Whichever order those happen in, a link added
/// while a worker was finishing is never left in the folder with no worker.
/// </remarks>
public sealed class NxmLinkQueue
{
    private const string LinkExtension = ".nxm";
    private readonly string _directory;

    public NxmLinkQueue(string directory)
    {
        _directory = directory;
    }

    /// <summary>
    /// The queue every Modular process shares, under <c>~/.config/Modular/nxm-queue</c>.
    /// </summary>
    public static NxmLinkQueue Default => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "Modular", "nxm-queue"));

    private string LockPath => Path.Combine(_directory, "worker.lock");

    /// <summary>Whether any links are waiting.</summary>
    public bool HasPending =>
        Directory.Exists(_directory) && Directory.EnumerateFiles(_directory, "*" + LinkExtension).Any();

    /// <summary>
    /// Adds a link. The file appears whole or not at all, so a worker never reads half of it.
    /// </summary>
    public void Add(string url)
    {
        Directory.CreateDirectory(_directory);
        // Ticks first, so links are taken in the order they were clicked.
        var name = $"{DateTime.UtcNow.Ticks:D20}-{Guid.NewGuid():N}";
        var temp = Path.Combine(_directory, name + ".tmp");
        File.WriteAllText(temp, url.Trim());
        File.Move(temp, Path.Combine(_directory, name + LinkExtension));
    }

    /// <summary>
    /// Becomes the worker if no other process is one.
    /// </summary>
    /// <returns>The lock, held until disposed; <c>null</c> when another process holds it.</returns>
    public IDisposable? TryBecomeWorker()
    {
        Directory.CreateDirectory(_directory);
        try
        {
            return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Removes and returns every waiting link, oldest first. Only the worker should call this.
    /// </summary>
    public IReadOnlyList<string> TakeAll()
    {
        if (!Directory.Exists(_directory))
            return [];

        var links = new List<string>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*" + LinkExtension).Order(StringComparer.Ordinal))
        {
            try
            {
                links.Add(File.ReadAllText(file));
                File.Delete(file);
            }
            catch (IOException)
            {
                // Gone already, or unreadable: drop it rather than stop the queue.
                try { File.Delete(file); } catch (IOException) { }
            }
        }
        return links;
    }
}
