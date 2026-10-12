using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Modular.Core.Metadata;
using Modular.Core.Utilities;
using Modular.Sdk.Archives;
using Modular.Sdk.Backends;

namespace Modular.Core.Dependencies.Conventions;

/// <summary>
/// Reads the ways other mod managers and mod hubs record where a mod came from and what
/// it depends on.
/// </summary>
/// <remarks>
/// <para>
/// None of this is Modular's own format. Mods arrive already carrying these records,
/// because they were packaged for another tool or downloaded through one:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Modular's download record</b>, <c>&lt;archive&gt;.modular.json</c>: written beside a
/// file Modular downloaded, and the only record that survives a rename.
/// </description></item>
/// <item><description>
/// <b>Mod Organizer 2</b>, <c>&lt;archive&gt;.meta</c>: an INI file beside each download with
/// the hub's mod and file ids.
/// </description></item>
/// <item><description>
/// <b>NexusMods file names</b>: <c>&lt;name&gt;-&lt;mod id&gt;-&lt;version&gt;-&lt;time&gt;</c>.
/// </description></item>
/// <item><description>
/// <b>Thunderstore / r2modman</b>, <c>manifest.json</c>: a <c>dependencies</c> list of
/// <c>Namespace-Name-Version</c> strings.
/// </description></item>
/// <item><description>
/// <b>FOMOD</b>, <c>fomod/info.xml</c> and <c>fomod/ModuleConfig.xml</c>: the mod's hub id,
/// and <c>moduleDependencies</c> naming files that must or must not be present.
/// </description></item>
/// <item><description>
/// <b>BG3 Mod Manager / Vortex</b>, <c>info.json</c>: the modules a pak archive contains.
/// </description></item>
/// </list>
/// <para>
/// Reading is best effort throughout. A malformed record is skipped, never an error: it
/// is somebody else's file, and a mod installs the same with or without it.
/// </para>
/// </remarks>
public static class DependencyConventions
{
    /// <summary>Suffix of the record Modular writes beside a file it downloads.</summary>
    public const string DownloadRecordSuffix = ".modular.json";

    /// <summary>Suffix of the record Mod Organizer 2 writes beside a download.</summary>
    public const string ModOrganizerSuffix = ".meta";

    // Metadata files are a few kilobytes; anything much larger is not what it claims.
    private const int MaxMetadataBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>
    /// Reads everything the records with and inside an archive declare.
    /// </summary>
    /// <param name="archivePath">Path of the archive. It need not still exist.</param>
    /// <param name="archiveReaderFactory">Opens the archive, when it does exist.</param>
    /// <returns>The declarations, most reliable source first; empty when there are none.</returns>
    public static ArchiveDeclarations Read(string? archivePath, IArchiveReaderFactory archiveReaderFactory)
    {
        ArgumentNullException.ThrowIfNull(archiveReaderFactory);

        if (string.IsNullOrWhiteSpace(archivePath))
            return ArchiveDeclarations.None;

        var declarations = ReadDownloadRecord(archivePath)
            .MergedWith(ReadModOrganizerMeta(archivePath));

        if (File.Exists(archivePath))
        {
            try
            {
                using var reader = archiveReaderFactory.Open(archivePath);
                if (reader != null)
                {
                    declarations = declarations
                        .MergedWith(ReadFomod(reader))
                        .MergedWith(ReadThunderstoreManifest(reader))
                        .MergedWith(ReadBg3Info(reader));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
            {
                // An archive that cannot be opened has nothing to add.
            }
        }

        // Last, because a file name is a guess where the others are records.
        if (NexusFileName.ParseModId(archivePath) is { } nexusId)
        {
            declarations = declarations.MergedWith(new ArchiveDeclarations
            {
                Source = new DeclaredSource(BackendIds.NexusMods, nexusId)
            });
        }

        return declarations;
    }

    /// <summary>
    /// Records where a downloaded file came from, beside the file.
    /// </summary>
    /// <param name="filePath">The downloaded file.</param>
    /// <param name="source">The hub mod it was downloaded from.</param>
    /// <remarks>Failure to write is ignored; the record is a convenience.</remarks>
    public static void WriteDownloadRecord(string filePath, DeclaredSource source)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(source);

        try
        {
            File.WriteAllText(
                filePath + DownloadRecordSuffix,
                JsonSerializer.Serialize(
                    new DownloadRecordFile
                    {
                        Backend = source.BackendId,
                        ModId = source.ModId,
                        GameDomain = source.GameDomain,
                        FileId = source.FileId
                    },
                    JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Reads Modular's own download record.</summary>
    /// <param name="archivePath">Path of the archive the record sits beside.</param>
    public static ArchiveDeclarations ReadDownloadRecord(string archivePath)
    {
        var text = ReadSidecar(archivePath + DownloadRecordSuffix);
        if (text == null)
            return ArchiveDeclarations.None;

        try
        {
            var record = JsonSerializer.Deserialize<DownloadRecordFile>(text, JsonOptions);

            return string.IsNullOrWhiteSpace(record?.Backend) || string.IsNullOrWhiteSpace(record.ModId)
                ? ArchiveDeclarations.None
                : new ArchiveDeclarations
                {
                    Source = new DeclaredSource(
                        record.Backend, record.ModId, NullIfEmpty(record.GameDomain), NullIfEmpty(record.FileId))
                };
        }
        catch (JsonException)
        {
            return ArchiveDeclarations.None;
        }
    }

    /// <summary>Reads the record Mod Organizer 2 keeps beside a download.</summary>
    /// <param name="archivePath">Path of the archive the record sits beside.</param>
    /// <remarks>
    /// Only <c>modID</c> and <c>fileID</c> are taken. MO2's <c>gameName</c> is its own
    /// short name for the game ("SkyrimSE"), not the hub's, so the game is left to be
    /// worked out from where the mod is installed.
    /// </remarks>
    public static ArchiveDeclarations ReadModOrganizerMeta(string archivePath)
    {
        var text = ReadSidecar(archivePath + ModOrganizerSuffix);
        if (text == null)
            return ArchiveDeclarations.None;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var separator = line.IndexOf('=');
            if (separator > 0)
                values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        if (!values.TryGetValue("modID", out var modId) ||
            !int.TryParse(modId, out var id) || id <= 0)
        {
            return ArchiveDeclarations.None;
        }

        // MO2 leaves the repository blank for a manual download and calls the hub "Nexus".
        var repository = values.GetValueOrDefault("repository", string.Empty);
        if (repository.Length > 0 && !repository.Equals("Nexus", StringComparison.OrdinalIgnoreCase))
            return ArchiveDeclarations.None;

        values.TryGetValue("fileID", out var fileId);

        return new ArchiveDeclarations
        {
            Source = new DeclaredSource(
                BackendIds.NexusMods, id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                FileId: int.TryParse(fileId, out var file) && file > 0 ? fileId : null),
            Name = NullIfEmpty(values.GetValueOrDefault("modName"))
        };
    }

    /// <summary>Reads a Thunderstore package manifest.</summary>
    /// <param name="reader">The open archive.</param>
    /// <remarks>
    /// The manifest gives the package's name but not its namespace, which only the
    /// dependency strings of other packages carry. A package therefore provides its bare
    /// name, and a dependency is matched on the name part of <c>Namespace-Name-Version</c>.
    /// </remarks>
    public static ArchiveDeclarations ReadThunderstoreManifest(IArchiveReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var text = ReadEntry(reader, e => Depth(e) == 0 && NameIs(e, "manifest.json"));
        if (text == null)
            return ArchiveDeclarations.None;

        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            var root = document.RootElement;

            // "version_number" is what makes it a Thunderstore manifest and not one of
            // the many other files called manifest.json.
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("version_number", out _) ||
                !root.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String)
            {
                return ArchiveDeclarations.None;
            }

            var requirements = new List<DeclaredRequirement>();

            if (root.TryGetProperty("dependencies", out var dependencies) &&
                dependencies.ValueKind == JsonValueKind.Array)
            {
                foreach (var dependency in dependencies.EnumerateArray())
                {
                    if (dependency.ValueKind != JsonValueKind.String)
                        continue;

                    // Namespace-Name-Version; a name may contain underscores but never a dash.
                    var parts = dependency.GetString()!.Split('-');
                    if (parts.Length < 3)
                        continue;

                    var (ns, name) = (parts[0], parts[1]);

                    requirements.Add(new DeclaredRequirement(
                        $"{name} by {ns}",
                        name,
                        "thunderstore",
                        Url: $"https://thunderstore.io/package/{ns}/{name}/"));
                }
            }

            var packageName = nameElement.GetString()!;

            return new ArchiveDeclarations
            {
                Name = packageName,
                Provides = [packageName],
                Requirements = requirements
            };
        }
        catch (JsonException)
        {
            return ArchiveDeclarations.None;
        }
    }

    /// <summary>Reads a FOMOD installer's metadata.</summary>
    /// <param name="reader">The open archive.</param>
    /// <remarks>
    /// <c>info.xml</c> may carry the mod's NexusMods id. <c>ModuleConfig.xml</c>'s top-level
    /// <c>moduleDependencies</c> name files the installer wants present ("Active") or
    /// absent ("Missing" or "Inactive") before it will run at all; conditions on
    /// individual install options are a matter for the installer, not for this.
    /// </remarks>
    public static ArchiveDeclarations ReadFomod(IArchiveReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        DeclaredSource? source = null;
        string? name = null;
        var requirements = new List<DeclaredRequirement>();

        if (ParseXml(ReadEntry(reader, e => PathEndsWith(e, "fomod/info.xml"))) is { Root: { } info })
        {
            name = NullIfEmpty(Child(info, "Name")?.Value.Trim());

            var id = Child(info, "Id")?.Value.Trim();
            var website = Child(info, "Website")?.Value ?? string.Empty;

            if (int.TryParse(id, out var modId) && modId > 0 &&
                website.Contains("nexusmods.com", StringComparison.OrdinalIgnoreCase))
            {
                source = new DeclaredSource(
                    BackendIds.NexusMods, modId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        if (ParseXml(ReadEntry(reader, e => PathEndsWith(e, "fomod/ModuleConfig.xml"))) is { Root: { } config } &&
            Child(config, "moduleDependencies") is { } moduleDependencies)
        {
            // "Or" means any one will do, which cannot be reported as "this is missing".
            var isAll = !string.Equals(
                (string?)moduleDependencies.Attribute("operator"), "Or", StringComparison.OrdinalIgnoreCase);

            foreach (var dependency in moduleDependencies.Elements()
                         .Where(e => e.Name.LocalName == "fileDependency" && isAll))
            {
                var file = ((string?)dependency.Attribute("file"))?.Replace('\\', '/').Trim();
                if (string.IsNullOrEmpty(file))
                    continue;

                var state = (string?)dependency.Attribute("state") ?? "Active";

                requirements.Add(new DeclaredRequirement(
                    Path.GetFileName(file),
                    file,
                    "fomod",
                    state.Equals("Active", StringComparison.OrdinalIgnoreCase)
                        ? DependencyType.Required
                        : DependencyType.Incompatible));
            }
        }

        return source == null && name == null && requirements.Count == 0
            ? ArchiveDeclarations.None
            : new ArchiveDeclarations { Source = source, Name = name, Requirements = requirements };
    }

    /// <summary>Reads the <c>info.json</c> BG3 Mod Manager and Vortex expect in a pak archive.</summary>
    /// <param name="reader">The open archive.</param>
    /// <remarks>
    /// Each listed module is something the archive provides, by UUID. A module's own
    /// dependencies are in its pak, which is read once the pak is installed.
    /// </remarks>
    public static ArchiveDeclarations ReadBg3Info(IArchiveReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var text = ReadEntry(reader, e => NameIs(e, "info.json"));
        if (text == null)
            return ArchiveDeclarations.None;

        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !TryGetIgnoreCase(document.RootElement, "Mods", out var mods) ||
                mods.ValueKind != JsonValueKind.Array)
            {
                return ArchiveDeclarations.None;
            }

            var provides = new List<string>();
            string? name = null;

            foreach (var mod in mods.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.Object))
            {
                if (TryGetIgnoreCase(mod, "UUID", out var uuid) && uuid.ValueKind == JsonValueKind.String &&
                    uuid.GetString() is { Length: > 0 } value)
                {
                    provides.Add(value.ToLowerInvariant());
                }

                if (name == null && TryGetIgnoreCase(mod, "Name", out var modName) &&
                    modName.ValueKind == JsonValueKind.String)
                {
                    name = NullIfEmpty(modName.GetString());
                }
            }

            return provides.Count == 0 && name == null
                ? ArchiveDeclarations.None
                : new ArchiveDeclarations { Name = name, Provides = provides };
        }
        catch (JsonException)
        {
            return ArchiveDeclarations.None;
        }
    }

    private static string? ReadSidecar(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= MaxMetadataBytes ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? ReadEntry(IArchiveReader reader, Func<ArchiveEntry, bool> match)
    {
        var entry = reader.Entries.FirstOrDefault(e => !e.IsDirectory && match(e));
        if (entry == null || entry.Length > MaxMetadataBytes)
            return null;

        try
        {
            using var stream = reader.OpenEntryStream(entry);
            using var text = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return text.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static XDocument? ParseXml(string? text)
    {
        if (text == null)
            return null;

        try
        {
            return XDocument.Parse(text);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    // FOMOD files are written by hand and by several tools; element case varies.
    private static XElement? Child(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(e =>
            string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));

    private static string Normalized(ArchiveEntry entry) =>
        entry.FullName.Replace('\\', '/').Trim('/');

    private static int Depth(ArchiveEntry entry) => Normalized(entry).Count(c => c == '/');

    private static bool NameIs(ArchiveEntry entry, string name)
    {
        var path = Normalized(entry);
        return string.Equals(path[(path.LastIndexOf('/') + 1)..], name, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathEndsWith(ArchiveEntry entry, string suffix)
    {
        var path = Normalized(entry);

        return path.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("/" + suffix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed class DownloadRecordFile
    {
        public string? Backend { get; set; }
        public string? ModId { get; set; }
        public string? GameDomain { get; set; }
        public string? FileId { get; set; }
    }
}
