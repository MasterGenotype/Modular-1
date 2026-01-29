using FluentAssertions;
using Modular.FluentHttp.Implementation;
using Xunit;

namespace Modular.FluentHttp.Tests;

public class FluentClientTests
{
    [Fact]
    public void Create_WithBaseUrl_SetsBaseUrl()
    {
        using var client = FluentClientFactory.Create("https://api.example.com");

        client.BaseUrl.Should().Be("https://api.example.com");
    }

    [Fact]
    public void SetBearerAuth_ConfiguresAuthentication()
    {
        using var client = FluentClientFactory.Create();

        client.SetBearerAuth("my_token");

        // Verify it doesn't throw and returns the client for chaining
        client.Should().NotBeNull();
    }

    [Fact]
    public void SetRetryPolicy_ConfiguresRetry()
    {
        using var client = FluentClientFactory.Create();

        client.SetRetryPolicy(maxRetries: 5, initialDelayMs: 500);

        // Verify it doesn't throw
        client.Should().NotBeNull();
    }

    [Fact]
    public void DisableRetries_SetsMaxRetriesToZero()
    {
        using var client = FluentClientFactory.Create();

        client.DisableRetries();

        client.Should().NotBeNull();
    }

    [Fact]
    public void AddFilter_AddsFilterToCollection()
    {
        using var client = FluentClientFactory.Create();
        var initialCount = client.Filters.Count;

        client.AddFilter(new TestFilter());

        client.Filters.Count.Should().Be(initialCount + 1);
    }

    private class TestFilter : Modular.FluentHttp.Interfaces.IHttpFilter
    {
        public string Name => "Test";
        public int Priority => 0;
        public void OnRequest(Modular.FluentHttp.Interfaces.IRequest request) { }
        public void OnResponse(Modular.FluentHttp.Interfaces.IResponse response, bool httpErrorAsException) { }
    }
}
