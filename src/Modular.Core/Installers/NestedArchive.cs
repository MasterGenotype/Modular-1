using Modular.Core.Archives;
using Modular.Sdk.Archives;
using Modular.Sdk.Installers;

namespace Modular.Core.Installers;

/// <summary>
/// A download that holds nothing but other archives, one per version of the mod, such as
/// an outfit packed as <c>shirt_angel.zip</c>, <c>shirt_vanilla.zip</c> and a readme.
/// The chosen inner archive is extracted to a temporary file and installed in its place.
/// </summary>
/// <remarks>
/// The versions are offered as an <see cref="InstallChoice"/>, so they are picked the
/// same way as any other alternative an archive offers. The changeset keeps the outer
/// path, so the install still traces back to the mod page it came from. The temporary
/// file is deleted on dispose.
/// </remarks>
public sealed class NestedArchive : IDisposable
{
    /// <summary>Id of the choice between the inner archives.</summary>
    public const string ChoiceId = "nested-archive";

    // Archives that can sit inside a download as one version of the mod.
    private static readonly HashSet<string> ArchiveExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z", ".rar" };

    // What else a wrapper carries for the reader rather than the game.
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".pdf", ".rtf", ".nfo", ".url", ".html", ".htm",
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"
    };

    private readonly IArchiveReaderFactory _factory;
    private readonly string _outerPath;
    private readonly IReadOnlyList<ArchiveEntry> _inner;
    private readonly string _directory;

    private NestedArchive(
        IArchiveReaderFactory factory, string outerPath, IReadOnlyList<ArchiveEntry> inner, string directory)
    {
        _factory = factory;
        _outerPath = outerPath;
        _inner = inner;
        _directory = directory;
        Choice = new InstallChoice
        {
            Id = ChoiceId,
            Title = "This download holds several versions. Which one?",
            Options = inner.Select(e => new InstallChoiceOption
            {
                Id = e.FullName,
                Label = Path.GetFileNameWithoutExtension(e.Name),
                FileCount = 1
            }).ToList()
        };
    }

    /// <summary>The extracted inner archive to read in place of the download.</summary>
    public string ArchivePath { get; private set; } = string.Empty;

    /// <summary>The choice between the inner archives, with the one in use selected.</summary>
    public InstallChoice Choice { get; }

    /// <summary>
    /// Opens a download whose only content is other archives, or returns <c>null</c> for
    /// anything else.
    /// </summary>
    /// <param name="path">The download.</param>
    /// <param name="selections">
    /// The user's answers, by choice id. The answer to <see cref="ChoiceId"/> names an
    /// inner archive by its path or its name without extension.
    /// </param>
    /// <param name="factory">Opens archives; the default reader when omitted.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The opened wrapper with the selected archive extracted, or <c>null</c>.</returns>
    public static async Task<NestedArchive?> OpenIfNeededAsync(
        string path,
        IReadOnlyDictionary<string, string> selections,
        IArchiveReaderFactory? factory = null,
        CancellationToken ct = default)
    {
        factory ??= new ArchiveReaderFactory();
        if (!factory.IsSupported(path))
            return null;

        List<ArchiveEntry> inner;
        using (var reader = factory.Open(path))
        {
            if (reader == null)
                return null;

            var files = reader.Entries.Where(e => !e.IsDirectory).ToList();
            inner = files.Where(e => ArchiveExtensions.Contains(Path.GetExtension(e.Name))).ToList();

            if (inner.Count == 0 ||
                files.Any(e => !inner.Contains(e) && !DocumentExtensions.Contains(Path.GetExtension(e.Name))))
            {
                return null;
            }
        }

        var directory = Path.Combine(Path.GetTempPath(), "modular-nested-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var nested = new NestedArchive(factory, path, inner.OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase).ToList(), directory);
        try
        {
            selections.TryGetValue(ChoiceId, out var given);
            await nested.SelectAsync(given, ct);
        }
        catch
        {
            nested.Dispose();
            throw;
        }

        return nested;
    }

    /// <summary>
    /// Extracts the inner archive an answer names, unless it is already the one in use.
    /// </summary>
    /// <param name="given">
    /// An inner archive's path or name without extension. With no answer that names one,
    /// the first is used, and the choice stays open unless it is the only one.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Whether a different archive is now in use.</returns>
    public async Task<bool> SelectAsync(string? given, CancellationToken ct = default)
    {
        var picked = given == null
            ? null
            : Choice.Options.FirstOrDefault(o =>
                string.Equals(o.Id, given, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(o.Label, given, StringComparison.OrdinalIgnoreCase));

        var option = picked ?? Choice.Options[0];
        Choice.IsResolved = picked != null || Choice.Options.Count == 1;

        if (ArchivePath.Length > 0 && string.Equals(Choice.SelectedOptionId, option.Id, StringComparison.Ordinal))
            return false;

        var entry = _inner.First(e => e.FullName == option.Id);
        var destination = Path.Combine(_directory, entry.Name);

        if (ArchivePath.Length > 0 && File.Exists(ArchivePath))
            File.Delete(ArchivePath);

        using (var reader = _factory.Open(_outerPath)
                   ?? throw new IOException($"Could not reopen {Path.GetFileName(_outerPath)}"))
        {
            var current = reader.Entries.First(e => e.FullName == entry.FullName);
            await reader.ExtractEntryAsync(current, destination, overwrite: true, ct);
        }

        ArchivePath = destination;
        Choice.SelectedOptionId = option.Id;
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover in the temp folder is harmless.
        }
    }
}
