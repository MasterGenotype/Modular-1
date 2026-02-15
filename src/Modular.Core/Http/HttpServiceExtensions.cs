using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Extensions.Http;
using System.Net;

namespace Modular.Core.Http;

/// <summary>
/// Extension methods for registering HTTP clients with IHttpClientFactory and Polly policies.
/// This replaces the custom FluentHttp and ModularHttpClient implementations with
/// the standard .NET approach for resilient HTTP communication.
/// </summary>
public static class HttpServiceExtensions
{
    /// <summary>
    /// Adds configured HTTP clients for all backends.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="nexusApiKey">NexusMods API key (optional, can be set later)</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddModularHttpClients(
        this IServiceCollection services,
        string? nexusApiKey = null)
    {
        // NexusMods API client with retry and circuit breaker
        services.AddHttpClient("nexusmods", client =>
        {
            client.BaseAddress = new Uri("https://api.nexusmods.com/");
            client.DefaultRequestHeaders.Add("accept", "application/json");
            client.DefaultRequestHeaders.Add("User-Agent", "Modular/1.0");
            if (!string.IsNullOrEmpty(nexusApiKey))
            {
                client.DefaultRequestHeaders.Add("apikey", nexusApiKey);
            }
        })
        .AddPolicyHandler(GetRetryPolicy())
        .AddPolicyHandler(GetCircuitBreakerPolicy());

        // GameBanana API client with lighter retry policy
        services.AddHttpClient("gamebanana", client =>
        {
            client.BaseAddress = new Uri("https://gamebanana.com/apiv11/");
            client.DefaultRequestHeaders.Add("User-Agent", "Modular/1.0");
        })
        .AddPolicyHandler(GetRetryPolicy(maxRetries: 2))
        .AddPolicyHandler(GetCircuitBreakerPolicy(exceptionsBeforeBreaking: 3));

        // Generic download client with longer timeout and decompression
        services.AddHttpClient("downloads", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(30);
            client.DefaultRequestHeaders.Add("User-Agent", "Modular/1.0");
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        })
        .AddPolicyHandler(GetRetryPolicy(maxRetries: 3, initialDelay: TimeSpan.FromSeconds(2)));

        return services;
    }

    /// <summary>
    /// Creates a retry policy with exponential backoff.
    /// </summary>
    /// <param name="maxRetries">Maximum number of retries</param>
    /// <param name="initialDelay">Initial delay before first retry</param>
    /// <returns>Async retry policy</returns>
    private static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy(
        int maxRetries = 3,
        TimeSpan? initialDelay = null)
    {
        var delay = initialDelay ?? TimeSpan.FromSeconds(1);

        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(msg => msg.StatusCode == HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(
                maxRetries,
                retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt - 1) * delay.TotalSeconds),
                onRetry: (outcome, timespan, retryAttempt, context) =>
                {
                    // Log retry attempt if logger is available
                    if (context.TryGetValue("logger", out var loggerObj) && loggerObj is ILogger logger)
                    {
                        logger.LogWarning(
                            "Request failed with {StatusCode}, retrying in {Delay}s (attempt {Attempt}/{MaxRetries})",
                            outcome.Result?.StatusCode ?? HttpStatusCode.ServiceUnavailable,
                            timespan.TotalSeconds,
                            retryAttempt,
                            maxRetries);
                    }
                });
    }

    /// <summary>
    /// Creates a circuit breaker policy to prevent hammering failed services.
    /// </summary>
    /// <param name="exceptionsBeforeBreaking">Number of exceptions before opening circuit</param>
    /// <param name="durationOfBreak">How long to keep circuit open</param>
    /// <returns>Circuit breaker policy</returns>
    private static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy(
        int exceptionsBeforeBreaking = 5,
        TimeSpan? durationOfBreak = null)
    {
        var breakDuration = durationOfBreak ?? TimeSpan.FromMinutes(1);

        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .CircuitBreakerAsync(
                exceptionsBeforeBreaking,
                breakDuration,
                onBreak: (outcome, breakDelay) =>
                {
                    // Circuit opened - service is failing
                },
                onReset: () =>
                {
                    // Circuit closed - service recovered
                },
                onHalfOpen: () =>
                {
                    // Circuit half-open - testing if service recovered
                });
    }
}
