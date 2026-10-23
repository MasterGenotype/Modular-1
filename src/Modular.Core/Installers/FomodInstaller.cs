using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Modular.Core.Archives;
using Modular.Core.Installers.Fomod;
using Modular.Core.Utilities;
using Modular.Sdk.Archives;
using Modular.Sdk.Installers;

namespace Modular.Core.Installers;

/// <summary>
/// Installer for FOMOD (Fallout Mod Manager) archives: runs the archive's
/// <c>fomod/ModuleConfig.xml</c> wizard and installs only the files its picks select.
/// </summary>
/// <remarks>
/// <para>
/// The wizard's decisions are offered as <see cref="InstallPlan.Choices"/>, answered from
/// <see cref="InstallContext.Selections"/> or else the script's defaults (see
/// <see cref="FomodPlanner"/>). Nothing outside what the script names is installed: the
/// wizard's images, the <c>fomod/</c> folder and every option not picked stay in the archive.
/// </para>
/// <para>
/// Destinations are the script's, relative to the game directory, as every FOMOD-capable
/// manager installs them.
/// </para>
/// </remarks>
public class FomodInstaller : IModInstaller
{
    private const string ConfigPath = "fomod/moduleconfig.xml";

    private readonly IArchiveReaderFactory _archiveReaderFactory;
    private readonly ILogger<FomodInstaller>? _logger;

    public string InstallerId => "fomod";
    public string DisplayName => "FOMOD Installer";
    public int Priority => 100; // High priority

    public FomodInstaller(IArchiveReaderFactory? archiveReaderFactory = null, ILogger<FomodInstaller>? logger = null)
    {
        _archiveReaderFactory = archiveReaderFactory ?? new ArchiveReaderFactory();
        _logger = logger;
    }

    /// <summary>
    /// Claims an archive whose <c>fomod/ModuleConfig.xml</c> installs something. A script
    /// that installs nothing (some archives carry one only for its description) is left to
    /// the game's own installer.
    /// </summary>
    public async Task<InstallDetectionResult> DetectAsync(string archivePath, CancellationToken ct = default)
    {
        try
        {
            using var reader = _archiveReaderFactory.Open(archivePath);
            if (reader == null)
            {
                return new InstallDetectionResult
                {
                    CanHandle = false,
                    Confidence = 0,
                    Reason = "Unable to open archive"
                };
            }

            var configEntry = FindConfig(reader.Entries, out _);
            if (configEntry == null)
            {
                return new InstallDetectionResult
                {
                    CanHandle = false,
                    Confidence = 0,
                    Reason = "No FOMOD configuration found"
                };
            }

            var config = await LoadConfigAsync(reader, configEntry, ct);
            if (!config.InstallsAnything)
            {
                return new InstallDetectionResult
                {
                    CanHandle = false,
                    Confidence = 0,
                    Reason = "FOMOD configuration installs no files"
                };
            }

            return new InstallDetectionResult
            {
                CanHandle = true,
                Confidence = 1.0,
                InstallerType = "fomod",
                Reason = "Contains FOMOD ModuleConfig.xml"
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Failed to detect FOMOD for {Path}", archivePath);
            return new InstallDetectionResult
            {
                CanHandle = false,
                Confidence = 0,
                Reason = $"Error: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Runs the wizard with the context's answers and plans the files the picks select.
    /// </summary>
    public async Task<InstallPlan> AnalyzeAsync(
        string archivePath,
        InstallContext context,
        CancellationToken ct = default)
    {
        using var reader = _archiveReaderFactory.Open(archivePath)
            ?? throw new InvalidOperationException($"Unable to open archive: {archivePath}");

        var configEntry = FindConfig(reader.Entries, out var root)
            ?? throw new InvalidOperationException("No fomod/ModuleConfig.xml found");

        var config = await LoadConfigAsync(reader, configEntry, ct);
        var archive = new ArchiveIndex(reader.Entries, root);

        var fomodPlan = FomodPlanner.Plan(
            config,
            context.Selections,
            file => FileState(context.GameDirectory, file),
            files => archive.Resolve(files).Count);

        var plan = new InstallPlan
        {
            InstallerId = InstallerId,
            SourcePath = archivePath,
            TargetDirectory = context.GameDirectory,
            RootDirectories = context.ModPaths?.Roots,
            Choices = fomodPlan.Choices.ToList(),
            RequiresUserInput = fomodPlan.Choices.Any(c => !c.IsResolved),
            Options = new Dictionary<string, object>
            {
                ["fomod_module_name"] = config.ModuleName ?? string.Empty,
                ["fomod_flags"] = fomodPlan.Flags.ToDictionary(f => f.Key, f => f.Value)
            }
        };

        foreach (var missing in archive.MissingSources(fomodPlan.Files))
        {
            _logger?.LogWarning(
                "FOMOD script for {Archive} names '{Source}', which the archive does not contain",
                Path.GetFileName(archivePath), missing);
        }

        foreach (var (entry, destination) in archive.Resolve(fomodPlan.Files))
        {
            plan.Operations.Add(new FileOperation
            {
                Type = FileOperationType.Extract,
                SourcePath = entry.FullName,
                DestinationPath = destination,
                SizeBytes = entry.Length,
                IsCritical = false
            });
            plan.TotalBytes += entry.Length;
        }

        return plan;
    }

    /// <summary>Extracts each planned file to its destination.</summary>
    public async Task<InstallResult> InstallAsync(
        InstallPlan plan,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var result = new InstallResult { Success = false };
        var installedFiles = new List<string>();

        try
        {
            using var reader = _archiveReaderFactory.Open(plan.SourcePath)
                ?? throw new InvalidOperationException($"Unable to open archive: {plan.SourcePath}");

            var entries = reader.Entries
                .Where(e => !e.IsDirectory)
                .GroupBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            // One operation per destination: a script may install the same source to two
            // places, which keying by source would collapse.
            var operations = plan.Operations
                .Where(o => o.Type == FileOperationType.Extract && !string.IsNullOrEmpty(o.SourcePath))
                .ToList();

            int filesProcessed = 0;
            long bytesProcessed = 0;

            foreach (var operation in operations)
            {
                ct.ThrowIfCancellationRequested();

                if (!entries.TryGetValue(operation.SourcePath, out var entry))
                    continue;

                var destPath = PathSanitizer.SanitizeEntryPath(
                    operation.DestinationPath, plan.TargetDirectory);

                var destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                await reader.ExtractEntryAsync(entry, destPath, overwrite: true, ct);
                installedFiles.Add(destPath);

                filesProcessed++;
                bytesProcessed += entry.Length;

                progress?.Report(new InstallProgress
                {
                    CurrentOperation = "Installing FOMOD",
                    CurrentFile = operation.DestinationPath,
                    FilesProcessed = filesProcessed,
                    TotalFiles = operations.Count,
                    BytesProcessed = bytesProcessed,
                    TotalBytes = plan.TotalBytes
                });
            }

            result.Success = true;
            result.InstalledFiles = installedFiles;
            result.Manifest = new InstallManifest
            {
                InstallerId = InstallerId,
                Files = installedFiles
            };

            _logger?.LogInformation("FOMOD installation completed: {Count} files", installedFiles.Count);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _logger?.LogError(ex, "FOMOD installation failed");
        }

        return result;
    }

    // The shallowest fomod/ModuleConfig.xml; root is the folder holding fomod/, which the
    // script's source paths are relative to.
    private static ArchiveEntry? FindConfig(IReadOnlyList<ArchiveEntry> entries, out string root)
    {
        var config = entries
            .Where(e => !e.IsDirectory)
            .Select(e => (Entry: e, Path: Normalize(e.FullName)))
            .Where(e => e.Path.Equals(ConfigPath, StringComparison.OrdinalIgnoreCase) ||
                        e.Path.EndsWith("/" + ConfigPath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Path.Count(c => c == '/'))
            .FirstOrDefault();

        root = config.Entry == null ? string.Empty : config.Path[..^ConfigPath.Length];
        return config.Entry;
    }

    // Authoring tools write UTF-16 and UTF-8 alike, not always with the declaration to
    // match; the bytes are trusted over the declaration.
    private static async Task<FomodModuleConfig> LoadConfigAsync(
        IArchiveReader reader, ArchiveEntry entry, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await using (var stream = reader.OpenEntryStream(entry))
            await stream.CopyToAsync(buffer, ct);

        var bytes = buffer.ToArray();
        try
        {
            using var parse = new MemoryStream(bytes);
            return FomodModuleConfig.Parse(XDocument.Load(parse));
        }
        catch (XmlException)
        {
            var text = Decode(bytes);
            var declarationEnd = text.StartsWith("<?xml", StringComparison.Ordinal) ? text.IndexOf("?>", StringComparison.Ordinal) : -1;
            if (declarationEnd >= 0)
                text = text[(declarationEnd + 2)..];

            return FomodModuleConfig.Parse(XDocument.Parse(text));
        }
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        var text = Encoding.UTF8.GetString(bytes);
        return text.TrimStart('﻿');
    }

    private static FomodFileState FileState(string gameDirectory, string file)
    {
        var relative = Normalize(file);
        return relative.Length > 0 && File.Exists(Path.Combine(gameDirectory, relative))
            ? FomodFileState.Active
            : FomodFileState.Missing;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim().Trim('/');

    /// <summary>
    /// The archive's files as the script sees them: relative to the folder holding
    /// <c>fomod/</c>, with either slash, in any case.
    /// </summary>
    private sealed class ArchiveIndex
    {
        private readonly string _root;
        private readonly List<(ArchiveEntry Entry, string Path)> _files;
        private readonly Dictionary<string, ArchiveEntry> _byPath;

        public ArchiveIndex(IReadOnlyList<ArchiveEntry> entries, string root)
        {
            _root = root;
            _files = entries
                .Where(e => !e.IsDirectory)
                .Select(e => (Entry: e, Path: Normalize(e.FullName)))
                .Where(e => e.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                .Select(e => (e.Entry, Path: e.Path[root.Length..]))
                .Where(e => !e.Path.StartsWith("fomod/", StringComparison.OrdinalIgnoreCase))
                .ToList();
            _byPath = _files
                .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Entry, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The archive file behind each destination the entries write, in the order the
        /// entries come; where two write one path the later wins.
        /// </summary>
        public List<(ArchiveEntry Entry, string Destination)> Resolve(IEnumerable<FomodFile> files)
        {
            var byDestination = new Dictionary<string, (ArchiveEntry Entry, string Destination)>(
                StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();

            void Add(ArchiveEntry entry, string destination)
            {
                if (destination.Length == 0)
                    return;
                if (!byDestination.ContainsKey(destination))
                    order.Add(destination);
                byDestination[destination] = (entry, destination);
            }

            foreach (var file in files)
            {
                var source = Normalize(file.Source);
                var destination = Normalize(file.Destination ?? string.Empty);

                if (file.IsFolder)
                {
                    var prefix = source.Length == 0 ? string.Empty : source + "/";
                    foreach (var (entry, path) in _files.Where(f =>
                                 f.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    {
                        var relative = path[prefix.Length..];
                        Add(entry, destination.Length == 0 ? relative : $"{destination}/{relative}");
                    }
                }
                else if (_byPath.TryGetValue(source, out var entry))
                {
                    // A file's destination is its full path; one ending in a slash names
                    // only the folder, and none at all keeps the source path.
                    var target = destination.Length == 0
                        ? source
                        : IsFolderPath(file.Destination!)
                            ? $"{destination}/{source[(source.LastIndexOf('/') + 1)..]}"
                            : destination;
                    Add(entry, target);
                }
            }

            return order.Select(d => byDestination[d]).ToList();
        }

        /// <summary>Sources the entries name that the archive does not hold.</summary>
        public IEnumerable<string> MissingSources(IEnumerable<FomodFile> files) =>
            files
                .Select(f => (File: f, Source: Normalize(f.Source)))
                .Where(f => f.File.IsFolder
                    ? f.Source.Length > 0 && !_files.Any(x =>
                        x.Path.StartsWith(f.Source + "/", StringComparison.OrdinalIgnoreCase))
                    : !_byPath.ContainsKey(f.Source))
                .Select(f => _root + f.Source)
                .Distinct(StringComparer.OrdinalIgnoreCase);

        private static bool IsFolderPath(string path) =>
            path.TrimEnd().EndsWith('/') || path.TrimEnd().EndsWith('\\');
    }
}
