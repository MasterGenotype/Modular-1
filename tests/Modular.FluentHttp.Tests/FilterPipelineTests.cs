using System.Net;
using FluentAssertions;
using Modular.FluentHttp.Filters;
using Modular.FluentHttp.Implementation;
using Xunit;

namespace Modular.FluentHttp.Tests;

public class FilterPipelineTests
{
    [Fact]
    public async Task AuthenticationFilter_HeaderIsSentOnFirstAttempt()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        using var client = new FluentClient(new HttpClient(handler));
        client.SetBaseUrl("https://api.example.com");
        client.AddFilter(new AuthenticationFilter("secret"));

        await client.GetAsync("resource").AsResponseAsync();

        handler.Requests.Should().HaveCount(1);
        handler.Requests[0].Headers.GetValues("apikey").Should().ContainSingle().Which.Should().Be("secret");
    }

    [Fact]
    public async Task ErrorFilter_NonRetryableStatus_IsNotRetried()
    {
        var handler = new StubHandler(HttpStatusCode.NotFound);
        using var client = new FluentClient(new HttpClient(handler));
        client.SetBaseUrl("https://api.example.com");
        client.SetRetryPolicy(maxRetries: 3, initialDelayMs: 1);
        client.AddFilter(new ErrorFilter());

        var act = () => client.GetAsync("missing").AsResponseAsync();

        (await act.Should().ThrowAsync<HttpRequestException>()).WithMessage("HTTP 404*");
        handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task ErrorFilter_RetryableStatus_ThrowsAfterRetriesAreExhausted()
    {
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable);
        using var client = new FluentClient(new HttpClient(handler));
        client.SetBaseUrl("https://api.example.com");
        client.SetRetryPolicy(maxRetries: 2, initialDelayMs: 1);
        client.AddFilter(new ErrorFilter());

        var act = () => client.GetAsync("flaky").AsResponseAsync();

        (await act.Should().ThrowAsync<HttpRequestException>()).WithMessage("HTTP 503*");
        handler.Requests.Should().HaveCount(3);
    }

    private class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public StubHandler(HttpStatusCode statusCode) => _statusCode = statusCode;

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(_statusCode));
        }
    }
}
