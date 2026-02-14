using Modular.Sdk;
using Modular.Sdk.Metadata;

namespace Modular.Examples.ExamplePlugin;

/// <summary>
/// Example plugin demonstrating how to create a metadata enricher.
/// This plugin shows the minimal implementation required.
/// </summary>
public class ExamplePluginMetadata : IPluginMetadata
{
    public string Id => "modular.example-plugin";
    public string DisplayName => "Example Metadata Enricher Plugin";
    public string Version => "1.0.0";
    public string? Description => "A reference implementation showing how to create a metadata enricher plugin";
    public string? Author => "Modular Team";
}

/// <summary>
/// Example metadata enricher that demonstrates the enricher interface.
/// In a real plugin, this would fetch data from an actual mod source API.
/// </summary>
public class ExampleMetadataEnricher : IMetadataEnricher
{
    public string BackendId => "example";

    public async Task<object> EnrichAsync(object backendMetadata, CancellationToken ct = default)
    {
        // In a real implementation, this would:
        // 1. Cast backendMetadata to the source-specific type
        // 2. Make API calls to fetch additional metadata
        // 3. Transform to canonical format
        // 4. Return a CanonicalMod object

        // For this example, we'll just demonstrate the structure
        var enriched = new
        {
            canonical_id = "example:123",
            name = "Example Mod",
            summary = "This is an example mod metadata",
            authors = new[] { new { name = "Example Author", id = "author123" } },
            tags = new[] { "example", "demo" },
            versions = new[]
            {
                new
                {
                    version_id = "v1.0.0",
                    version_number = "1.0.0",
                    release_channel = "stable",
                    changelog = "Initial release",
                    files = new[]
                    {
                        new
                        {
                            file_id = "file123",
                            file_name = "example-mod-v1.0.0.zip",
                            size_bytes = 1024000
                        }
                    }
                }
            }
        };

        return await Task.FromResult(enriched);
    }
}
