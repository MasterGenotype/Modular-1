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

    // WebSocket ping control frames, as Nexus's SSO demo sends with ws.ping().
    // A data frame is not a ping: the server parses it as a message, answers
    // "Invalid JSON" and the login fails.
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PongTimeout = TimeSpan.FromSeconds(20);

    private readonly string _applicationSlug;
    private readonly ILogger? _logger;

    public NexusSsoClient(string applicationSlug = "vortex", ILogger? logger = null)
    {
        _applicationSlug = applicationSlug;
        _logger = logger;
    }

    /// <summary>
    /// Runs the full SSO flow: connects to the SSO WebSocket, opens the
    /// authorization page in the user's browser, and waits for the API key.
    /// </summary>
    /// <param name="onAuthorizationUrl">
    /// Receives the authorization page URL, so it can be shown to the user in
    /// case the browser does not open.
    /// </param>
    /// <returns>The NexusMods API key.</returns>
    /// <exception cref="TimeoutException">Thrown if the user doesn't authorize within the timeout.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the SSO server returns an error.</exception>
    public async Task<string> AuthenticateAsync(
        Action<string>? onAuthorizationUrl = null,
        CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Timeout);

        try
        {
            return await RunAsync(onAuthorizationUrl, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                "SSO authorization timed out. The user did not authorize within the time limit.");
        }
    }

    private async Task<string> RunAsync(Action<string>? onAuthorizationUrl, CancellationToken ct)
    {
        var uuid = Guid.NewGuid().ToString();

        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = PingInterval;
        ws.Options.KeepAliveTimeout = PongTimeout;
        await ws.ConnectAsync(new Uri(SsoEndpoint), ct);
        _logger?.LogInformation("Connected to NexusMods SSO server");

        var handshake = JsonSerializer.Serialize(new SsoHandshake
        {
            Id = uuid,
            Token = null,
            Protocol = 2
        });
        await SendAsync(ws, handshake, ct);

        var tokenResponse = Parse(await ReceiveAsync(ws, ct));
        if (tokenResponse?.Success != true || tokenResponse.Data?.ConnectionToken == null)
            throw new InvalidOperationException(
                $"SSO handshake failed: {tokenResponse?.Error ?? "no connection token received"}");
        _logger?.LogDebug("Received connection token");

        var authUrl = $"{AuthUrlBase}?id={uuid}&application={Uri.EscapeDataString(_applicationSlug)}";
        onAuthorizationUrl?.Invoke(authUrl);
        OpenBrowser(authUrl);

        while (true)
        {
            var response = Parse(await ReceiveAsync(ws, ct));

            if (response?.Success == true && !string.IsNullOrEmpty(response.Data?.ApiKey))
            {
                _logger?.LogInformation("Received API key from SSO");
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
                return response.Data.ApiKey;
            }

            if (response?.Error != null)
                throw new InvalidOperationException($"SSO error: {response.Error}");
        }

        static SsoResponse? Parse(string message)
        {
            try
            {
                return JsonSerializer.Deserialize<SsoResponse>(message);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    private static async Task SendAsync(ClientWebSocket ws, string message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    // Reads one whole text message, however many frames it arrives in.
    private static async Task<string> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException(
                    $"SSO server closed the connection unexpectedly: {result.CloseStatus} {result.CloseStatusDescription}".TrimEnd());
            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
    }

    private void OpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", url);
            else
                System.Diagnostics.Process.Start("xdg-open", url);
        }
        catch (Exception ex)
        {
            // The URL has already gone to onAuthorizationUrl, so the user can open it by hand.
            _logger?.LogWarning(ex, "Could not open a browser for {Url}", url);
        }
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
