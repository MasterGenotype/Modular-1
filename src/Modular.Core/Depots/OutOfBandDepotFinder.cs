namespace Modular.Core.Depots;

/// <summary>
/// Finds depots whose files are in an install folder but which the acf doesn't list, such as
/// tool depots added with the Steam console's <c>download_depot</c> (Cyberpunk's REDmod,
/// depot 2060310). Steam doesn't know these are installed, so it never updates them.
/// </summary>
public static class OutOfBandDepotFinder
{
    private const int MinFiles = 3;
    private const int ScreenSample = 16;
    private const double ScreenRatio = 0.75;
    private const double PresentRatio = 0.9;

    /// <param name="exclude">Depots to skip: the acf's installed depots and the app id itself
    /// (that depot only carries shader-cache manifests).</param>
    public static IReadOnlyList<OutOfBandDepot> Find(
        string installPath, DepotManifestStore store, IReadOnlySet<uint> exclude, CancellationToken ct = default)
    {
        var found = new List<OutOfBandDepot>();
        // Newest archived manifest per depot is enough to recognise the depot's layout.
        foreach (var group in store.List().GroupBy(m => m.DepotId))
        {
            ct.ThrowIfCancellationRequested();
            if (exclude.Contains(group.Key))
                continue;

            ManifestView view;
            try
            {
                var manifest = store.Load(group.Key, group.First().ManifestId);
                if (manifest is null)
                    continue;
                view = ManifestView.From(manifest);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
            {
                continue;
            }
            if (view.Files.Count < MinFiles)
                continue;

            bool Exists(ManifestFile f) => File.Exists(Path.Combine(installPath, f.Path));

            var sample = view.Files.Take(ScreenSample).ToList();
            if (sample.Count(Exists) < sample.Count * ScreenRatio)
                continue;

            var present = view.Files.Count(Exists);
            var ratio = (double)present / view.Files.Count;
            if (ratio >= PresentRatio)
                found.Add(new OutOfBandDepot(group.Key, group.Select(m => m.ManifestId).ToList(), ratio));
        }
        return found;
    }
}

public sealed record OutOfBandDepot(uint DepotId, IReadOnlyList<ulong> ArchivedManifests, double PresentRatio);
