using Modular.Core.Authentication;
using Modular.Core.Configuration;
using Xunit;

namespace Modular.Core.Tests;

/// <summary>Tests that set process-wide environment variables run apart from the rest.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentCollection
{
    public const string Name = "Environment";
}

[Collection(EnvironmentCollection.Name)]
public sealed class NexusKeyPrecedenceTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "modular-login-tests", Guid.NewGuid().ToString("N"));
    private readonly string? _previousNexusKey = Environment.GetEnvironmentVariable("NEXUS_API_KEY");
    private readonly string? _previousApiKey = Environment.GetEnvironmentVariable("API_KEY");

    public NexusKeyPrecedenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("NEXUS_API_KEY", _previousNexusKey);
        Environment.SetEnvironmentVariable("API_KEY", _previousApiKey);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task LoadAsync_KeepsSsoKeyOverEnvironmentKey()
    {
        var path = await WriteConfigAsync(new AppSettings { NexusApiKey = "from-sso", NexusApiKeyFromSso = true });
        Environment.SetEnvironmentVariable("NEXUS_API_KEY", "from-env");

        var settings = await new ConfigurationService().LoadAsync(path);

        Assert.Equal("from-sso", settings.NexusApiKey);
        Assert.True(settings.NexusApiKeyFromSso);
    }

    [Fact]
    public async Task LoadAsync_EnvironmentKeyReplacesHandEnteredKey()
    {
        var path = await WriteConfigAsync(new AppSettings { NexusApiKey = "pasted" });
        Environment.SetEnvironmentVariable("NEXUS_API_KEY", "from-env");

        var settings = await new ConfigurationService().LoadAsync(path);

        Assert.Equal("from-env", settings.NexusApiKey);
        Assert.False(settings.NexusApiKeyFromSso);
    }

    [Fact]
    public async Task LoadAsync_EnvironmentKeyFillsMissingKey()
    {
        var path = await WriteConfigAsync(new AppSettings { NexusApiKeyFromSso = true });
        Environment.SetEnvironmentVariable("NEXUS_API_KEY", "from-env");

        var settings = await new ConfigurationService().LoadAsync(path);

        Assert.Equal("from-env", settings.NexusApiKey);
    }

    private async Task<string> WriteConfigAsync(AppSettings settings)
    {
        var path = Path.Combine(_dir, "config.json");
        await new ConfigurationService().SaveAsync(settings, path);
        return path;
    }
}

public sealed class NexusLoginTests
{
    // Neither case reaches the SSO server: the client is never called.
    private static NexusLogin Create(AppSettings settings) =>
        new(settings, new ConfigurationService(), new NexusSsoClient());

    [Fact]
    public async Task EnsureKeyAsync_UsesExistingKeyWithoutLoggingIn()
    {
        var login = Create(new AppSettings { NexusApiKey = "existing" });

        Assert.True(await login.EnsureKeyAsync());
    }

    [Fact]
    public async Task EnsureKeyAsync_ReportsNoKeyWhenSsoIsOff()
    {
        var login = Create(new AppSettings { NexusSsoEnabled = false });

        Assert.False(await login.EnsureKeyAsync());
    }
}
