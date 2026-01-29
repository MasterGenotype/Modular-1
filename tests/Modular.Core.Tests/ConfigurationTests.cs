using FluentAssertions;
using Modular.Core.Configuration;
using Modular.Core.Exceptions;
using Xunit;

namespace Modular.Core.Tests;

public class ConfigurationTests
{
    [Fact]
    public void AppSettings_DefaultValues_AreCorrect()
    {
        var settings = new AppSettings();

        settings.NexusApiKey.Should().BeEmpty();
        settings.GameBananaUserId.Should().BeEmpty();
        settings.DefaultCategories.Should().BeEquivalentTo(["main", "optional"]);
        settings.AutoRename.Should().BeTrue();
        settings.OrganizeByCategory.Should().BeTrue();
        settings.VerifyDownloads.Should().BeFalse();
        settings.MaxConcurrentDownloads.Should().Be(1);
    }

    [Fact]
    public void ConfigurationService_Validate_ThrowsWhenNexusKeyRequired()
    {
        var service = new ConfigurationService();
        var settings = new AppSettings();

        var act = () => service.Validate(settings, requireNexusKey: true);

        act.Should().Throw<ConfigException>()
            .Where(e => e.ConfigKey == "nexus_api_key");
    }

    [Fact]
    public void ConfigurationService_Validate_ThrowsWhenGameBananaIdRequired()
    {
        var service = new ConfigurationService();
        var settings = new AppSettings();

        var act = () => service.Validate(settings, requireGameBananaId: true);

        act.Should().Throw<ConfigException>()
            .Where(e => e.ConfigKey == "gamebanana_user_id");
    }

    [Fact]
    public void ConfigurationService_Validate_PassesWithValidConfig()
    {
        var service = new ConfigurationService();
        var settings = new AppSettings
        {
            NexusApiKey = "test_key",
            GameBananaUserId = "12345"
        };

        var act = () => service.Validate(settings, requireNexusKey: true, requireGameBananaId: true);

        act.Should().NotThrow();
    }
}
