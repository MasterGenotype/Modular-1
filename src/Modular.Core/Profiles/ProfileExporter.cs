using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Modular.Core.Dependencies;

namespace Modular.Core.Profiles;

/// <summary>
/// Handles profile export and import for modpack sharing.
/// </summary>
public class ProfileExporter
{
    private readonly ILogger<ProfileExporter>? _logger;

    public ProfileExporter(ILogger<ProfileExporter>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Exports a profile to a portable format.
    /// </summary>
    public async Task<ExportResult> ExportProfileAsync(
        ModProfile profile,
        ModLockfile lockfile,
        string outputPath,
        ExportOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new ExportOptions();
        var result = new ExportResult { ProfileName = profile.Name };

        try
        {
            var export = new ProfileExport
            {
                Version = 1,
                ExportedAt = DateTime.UtcNow,
                Profile = profile,
                Lockfile = lockfile,
                Metadata = options.Metadata ?? new Dictionary<string, string>()
            };

            // Add export metadata
            export.Metadata["exporter_version"] = typeof(ProfileExporter).Assembly.GetName().Version?.ToString() ?? "unknown";
            export.Metadata["platform"] = Environment.OSVersion.ToString();

            if (options.Format == ExportFormat.Json)
            {
                await ExportAsJsonAsync(export, outputPath, ct);
            }
            else if (options.Format == ExportFormat.Archive)
            {
                await ExportAsArchiveAsync(export, outputPath, options, ct);
            }

            result.Success = true;
            result.OutputPath = outputPath;
            result.FileSize = new FileInfo(outputPath).Length;

            _logger?.LogInformation(
                "Exported profile {Name} to {Path} ({Size} bytes)",
                profile.Name, outputPath, result.FileSize);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _logger?.LogError(ex, "Failed to export profile {Name}", profile.Name);
        }

        return result;
    }

    /// <summary>
    /// Imports a profile from a file.
    /// </summary>
    public async Task<ImportResult> ImportProfileAsync(
        string inputPath,
        ImportOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new ImportOptions();
        var result = new ImportResult { InputPath = inputPath };

        try
        {
            ProfileExport? export = null;

            // Detect format
            var extension = Path.GetExtension(inputPath).ToLowerInvariant();
            if (extension == ".json")
            {
                export = await ImportFromJsonAsync(inputPath, ct);
            }
            else if (extension == ".zip" || extension == ".modpack")
            {
                export = await ImportFromArchiveAsync(inputPath, ct);
            }
            else
            {
                throw new InvalidOperationException($"Unsupported file format: {extension}");
            }

            if (export == null)
            {
                throw new InvalidOperationException("Failed to load profile export");
            }

            // Validate export
            var validation = ValidateExport(export);
            if (!validation.IsValid)
            {
                result.Success = false;
                result.Error = $"Validation failed: {string.Join(", ", validation.Errors)}";
                result.ValidationErrors = validation.Errors;
                return result;
            }

            result.Success = true;
            result.Profile = export.Profile;
            result.Lockfile = export.Lockfile;
            result.Metadata = export.Metadata;
            result.ValidationWarnings = validation.Warnings;

            _logger?.LogInformation(
                "Imported profile {Name} from {Path}",
                export.Profile.Name, inputPath);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _logger?.LogError(ex, "Failed to import profile from {Path}", inputPath);
        }

        return result;
    }

    /// <summary>
    /// Validates an exported profile.
    /// </summary>
    public ExportValidation ValidateExport(ProfileExport export)
    {
        var validation = new ExportValidation { IsValid = true };

        // Check version compatibility
        if (export.Version > 1)
            validation.Warnings.Add($"Export version {export.Version} is newer than supported version 1");

        // Validate profile
        if (export.Profile == null)
        {
            validation.IsValid = false;
            validation.Errors.Add("Profile is missing");
            return validation;
        }

        if (string.IsNullOrEmpty(export.Profile.Name))
            validation.Errors.Add("Profile name is empty");

        // Validate lockfile
        if (export.Lockfile == null)
            validation.Warnings.Add("Lockfile is missing - dependency resolution will be required");
        else
        {
            // Check lockfile integrity
            if (export.Lockfile.Mods.Count == 0)
                validation.Warnings.Add("Lockfile contains no mods");
        }

        validation.IsValid = validation.Errors.Count == 0;
        return validation;
    }

    private async Task ExportAsJsonAsync(ProfileExport export, string outputPath, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(export, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(outputPath, json, ct);
    }

    private async Task ExportAsArchiveAsync(
        ProfileExport export,
        string outputPath,
        ExportOptions options,
        CancellationToken ct)
    {
        using var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create);

        // Add profile.json
        var profileEntry = archive.CreateEntry("profile.json");
        await using (var entryStream = profileEntry.Open())
        {
            await JsonSerializer.SerializeAsync(entryStream, export, cancellationToken: ct);
        }

        // Add README if provided
        if (!string.IsNullOrEmpty(options.ReadmeContent))
        {
            var readmeEntry = archive.CreateEntry("README.md");
            await using var readmeStream = readmeEntry.Open();
            await using var writer = new StreamWriter(readmeStream);
            await writer.WriteAsync(options.ReadmeContent);
        }

        _logger?.LogDebug("Created archive with {Count} entries", archive.Entries.Count);
    }

    private async Task<ProfileExport?> ImportFromJsonAsync(string inputPath, CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(inputPath, ct);
        return JsonSerializer.Deserialize<ProfileExport>(json);
    }

    private async Task<ProfileExport?> ImportFromArchiveAsync(string inputPath, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(inputPath);
        var profileEntry = archive.GetEntry("profile.json");

        if (profileEntry == null)
            throw new InvalidOperationException("Archive does not contain profile.json");

        await using var stream = profileEntry.Open();
        return await JsonSerializer.DeserializeAsync<ProfileExport>(stream, cancellationToken: ct);
    }
}

/// <summary>
/// Portable profile export format.
/// </summary>
public class ProfileExport
{
    public int Version { get; set; } = 1;
    public DateTime ExportedAt { get; set; }
    public ModProfile Profile { get; set; } = null!;
    public ModLockfile? Lockfile { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}

/// <summary>
/// Options for profile export.
/// </summary>
public class ExportOptions
{
    public ExportFormat Format { get; set; } = ExportFormat.Json;
    public Dictionary<string, string>? Metadata { get; set; }
    public string? ReadmeContent { get; set; }
}

/// <summary>
/// Export format types.
/// </summary>
public enum ExportFormat
{
    Json,
    Archive
}

/// <summary>
/// Options for profile import.
/// </summary>
public class ImportOptions
{
    public bool ValidateIntegrity { get; set; } = true;
    public bool ResolveOnImport { get; set; } = true;
}

/// <summary>
/// Result of profile export.
/// </summary>
public class ExportResult
{
    public bool Success { get; set; }
    public string ProfileName { get; set; } = string.Empty;
    public string? OutputPath { get; set; }
    public long FileSize { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Result of profile import.
/// </summary>
public class ImportResult
{
    public bool Success { get; set; }
    public string InputPath { get; set; } = string.Empty;
    public ModProfile? Profile { get; set; }
    public ModLockfile? Lockfile { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
    public List<string> ValidationErrors { get; set; } = new();
    public List<string> ValidationWarnings { get; set; } = new();
    public string? Error { get; set; }
}

/// <summary>
/// Validation result for profile export.
/// </summary>
public class ExportValidation
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}
