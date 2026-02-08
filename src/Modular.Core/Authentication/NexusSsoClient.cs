using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Modular.Core.Authentication;

/// <summary>
/// Handles the NexusMods SSO WebSocket flow to obtain an API key.
/// </summary>
public class NexusSsoClient
{
    private const string SsoEndpoint = "wss://sso.nexusmods.com";
    private const string AuthUrlBase = "https://www.nexusmods.com/sso";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);

    private readonly string _applicationSlug;
    private readonly ILogger? _logger;

    public NexusSsoClient(string applicationSlug = "modular", ILogger? logger = null)
    {
        _applicationSlug = applicationSlug;
        _logger = logger;
    }

    /// <summary>
    /// Runs the full SSO flow: connects to the SSO WebSocket, opens the
    /// authorization page in the user's browser, and waits for the API key.
    /// </summary>
    /// <returns>The NexusMods API key.</returns>
    /// <exception cref="TimeoutException">Thrown if the user doesn't authorize within the timeout.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the SSO server returns an error.</exception>
    public async Task<string> AuthenticateAsync(CancellationToken ct = default)
    {
        var uuid = Guid.NewGuid().ToString();
        string? connectionToken = null;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Timeout);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(SsoEndpoint), timeoutCts.Token);
        _logger?.LogInformation("Connected to NexusMods SSO server");

        // Send handshake
        var handshake = JsonSerializer.Serialize(new SsoHandshake
        {
            Id = uuid,
            Token = connectionToken,
            Protocol = 2
        });
        await SendAsync(ws, handshake, timeoutCts.Token);

        // Receive connection token
        var response = await ReceiveAsync(ws, timeoutCts.Token);
        var tokenResponse = JsonSerializer.Deserialize<SsoResponse>(response);
        if (tokenResponse?.Success == true && tokenResponse.Data?.ConnectionToken != null)
        {
            connectionToken = tokenResponse.Data.ConnectionToken;
            _logger?.LogDebug("Received connection token");
        }

        // Open browser for authorization
        var authUrl = $"{AuthUrlBase}?id={uuid}&application={_applicationSlug}";
        OpenBrowser(authUrl);

        // Wait for API key with periodic pings
        return await WaitForApiKeyAsync(ws, timeoutCts.Token);
    }

    private async Task<string> WaitForApiKeyAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[4096];

        while (!ct.IsCancellationRequested)
        {
            // Use a short receive timeout so we can send pings
            using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pingCts.CancelAfter(PingInterval);

            try
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), pingCts.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException("SSO server closed the connection unexpectedly.");

                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var response = JsonSerializer.Deserialize<SsoResponse>(message);

                if (response?.Success == true && response.Data?.ApiKey != null)
                {
                    _logger?.LogInformation("Received API key from SSO");
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
                    return response.Data.ApiKey;
                }

                if (response?.Error != null)
                    throw new InvalidOperationException($"SSO error: {response.Error}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Ping timeout - send ping to keep connection alive
                await ws.SendAsync(
                    ArraySegment<byte>.Empty,
                    WebSocketMessageType.Binary,
                    true,
                    ct);
            }
        }

        throw new TimeoutException("SSO authorization timed out. The user did not authorize within the time limit.");
    }

    private static async Task SendAsync(ClientWebSocket ws, string message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    private static async Task<string> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private static void OpenBrowser(string url)
    {
        // Cross-platform browser launch
        if (OperatingSystem.IsWindows())
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS())
            System.Diagnostics.Process.Start("open", url);
        else
            System.Diagnostics.Process.Start("xdg-open", url);
    }
}

// --- JSON Models ---

file class SsoHandshake
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("protocol")]
    public int Protocol { get; set; }
}

file class SsoResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("data")]
    public SsoResponseData? Data { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

file class SsoResponseData
{
    [JsonPropertyName("connection_token")]
    public string? ConnectionToken { get; set; }

    [JsonPropertyName("api_key")]
    public string? ApiKey { get; set; }
}
