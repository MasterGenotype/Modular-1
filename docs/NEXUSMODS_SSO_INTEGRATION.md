# NexusMods SSO Integration for Modular

This document describes how to integrate the NexusMods Single Sign-On (SSO) system into Modular, replacing the current manual API key configuration with an interactive browser-based authorization flow.

## Table of Contents

1. [Background](#background)
2. [How NexusMods SSO Works](#how-nexusmods-sso-works)
3. [Integration Plan](#integration-plan)
4. [Implementation Steps](#implementation-steps)
5. [File-by-File Changes](#file-by-file-changes)
6. [Error Handling](#error-handling)
7. [Testing](#testing)
8. [References](#references)

---

## Background

### Current State

Modular currently requires users to manually obtain and configure a NexusMods API key:

- Users visit `https://www.nexusmods.com/users/myaccount?tab=api` to get a personal API key
- The key is stored in `~/.config/Modular/config.json` as `nexus_api_key` or via the `NEXUS_API_KEY` environment variable
- Every API request attaches this key via `.WithHeader("apikey", _settings.NexusApiKey)`

### Goal

Add an SSO flow so Modular can obtain an API key automatically through a browser-based authorization, similar to how Vortex and MO2 authenticate. The manual API key path should remain as a fallback.

---

## How NexusMods SSO Works

The NexusMods SSO uses a **WebSocket-based protocol** (not OAuth/OIDC). The flow is:

```
Modular                    SSO Server (wss://sso.nexusmods.com)         Browser
  │                                │                                       │
  │─── WebSocket connect ─────────>│                                       │
  │─── { id, token, protocol:2 } ─>│                                       │
  │<── { connection_token } ───────│                                       │
  │                                │                                       │
  │── Open browser ───────────────────────────────────────────────────────>│
  │    https://www.nexusmods.com/sso?id={uuid}&application={app_slug}     │
  │                                │                                       │
  │                                │<── User logs in and clicks Authorize ─│
  │<── { api_key } ───────────────│                                       │
  │                                │                                       │
  │── Close WebSocket ────────────>│                                       │
```

### Protocol Details

**1. Generate a UUID v4**

A random UUID identifies this SSO session. Example: `550e8400-e29b-41d4-a716-446655440000`.

**2. Connect to the SSO WebSocket**

```
Endpoint: wss://sso.nexusmods.com
```

**3. Send the handshake message**

```json
{
  "id": "<uuid>",
  "token": null,
  "protocol": 2
}
```

- `id` - The UUID generated in step 1
- `token` - `null` for first connection; use the `connection_token` from a previous response if reconnecting
- `protocol` - Must be `2`

**4. Receive the connection token**

```json
{
  "success": true,
  "data": {
    "connection_token": "<token_string>"
  },
  "error": null
}
```

Store this `connection_token` to handle reconnections (e.g., network drops).

**5. Open the authorization URL in the user's browser**

```
https://www.nexusmods.com/sso?id=<uuid>&application=<application_slug>
```

- The user will see a login page (if not already logged in) followed by an authorization prompt
- `application_slug` must be registered with NexusMods staff (see [Application Registration](#application-registration))

**6. Receive the API key**

Once the user authorizes, the server sends the API key as a plain string in the WebSocket data field:

```json
{
  "success": true,
  "data": {
    "api_key": "<the_api_key>"
  },
  "error": null
}
```

This is the **only non-error data message** you will receive. Save the key and close the connection.

**7. Keepalive**

Send a WebSocket ping every **30 seconds** to keep the connection alive while waiting for user authorization.

### Application Registration

An `application_slug` can only be created by NexusMods staff. Contact the [NexusMods Community Team](https://www.nexusmods.com/contact) to register Modular as an application. You will receive an `application_slug` (e.g., `"modular"`).

Until registration is complete, you can test with the slug `"vortex"` for development purposes only.

---

## Integration Plan

### Architecture

Create a new `NexusSsoClient` class in `Modular.Core` that encapsulates the entire SSO flow. The CLI layer will call this when no API key is configured.

```
Modular.Cli (Program.cs)
    │
    ├── Has API key? ──> Use it directly (existing flow)
    │
    └── No API key? ──> NexusSsoClient.AuthenticateAsync()
                            │
                            ├── Open WebSocket to wss://sso.nexusmods.com
                            ├── Open browser to authorization URL
                            ├── Wait for API key via WebSocket
                            ├── Save key to config.json
                            └── Return key
```

### Design Decisions

1. **Separate class, not embedded in NexusModsBackend** - The SSO flow is a one-time setup concern, not a per-request concern. Keep it isolated.
2. **Store key in config.json** - After SSO completes, write the key to the config file so subsequent runs don't need SSO again.
3. **Keep manual key as fallback** - Environment variables and manual config should always work (headless servers, CI, etc.).
4. **Timeout** - The SSO flow should timeout after 5 minutes if the user doesn't authorize.
5. **No new dependencies** - Use `System.Net.WebSockets.ClientWebSocket` from the .NET BCL.

---

## Implementation Steps

### Step 1: Create the SSO Client

Create `src/Modular.Core/Authentication/NexusSsoClient.cs`:

```csharp
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
    private const string ApplicationSlug = "modular"; // Must be registered with NexusMods
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);

    private readonly ILogger? _logger;

    public NexusSsoClient(ILogger? logger = null)
    {
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
        var authUrl = $"{AuthUrlBase}?id={uuid}&application={ApplicationSlug}";
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
```

### Step 2: Add SSO Configuration Properties

Add to `src/Modular.Core/Configuration/AppSettings.cs`:

```csharp
/// <summary>
/// Application slug registered with NexusMods for SSO.
/// </summary>
[JsonPropertyName("nexus_application_slug")]
public string NexusApplicationSlug { get; set; } = "modular";

/// <summary>
/// Whether to use SSO to obtain the NexusMods API key interactively.
/// When true and no API key is configured, Modular will open the browser
/// for authorization. Set to false for headless/CI environments.
/// </summary>
[JsonPropertyName("nexus_sso_enabled")]
public bool NexusSsoEnabled { get; set; } = true;
```

### Step 3: Update ConfigurationService

In `src/Modular.Core/Configuration/ConfigurationService.cs`, add a method to save the API key back to the config file after SSO:

```csharp
/// <summary>
/// Saves the NexusMods API key to the config file after SSO authentication.
/// </summary>
public async Task SaveApiKeyAsync(AppSettings settings)
{
    var configPath = GetConfigPath();
    var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    });
    await File.WriteAllTextAsync(configPath, json);
}
```

### Step 4: Update the CLI Entry Point

In `src/Modular.Cli/Program.cs`, modify `InitializeServices()` to attempt SSO when no API key is present:

```csharp
static async Task<(AppSettings settings, ...)> InitializeServices()
{
    var configService = new ConfigurationService();
    var settings = await configService.LoadAsync();

    // If no API key and SSO is enabled, run the SSO flow
    if (string.IsNullOrWhiteSpace(settings.NexusApiKey) && settings.NexusSsoEnabled)
    {
        Console.WriteLine("No NexusMods API key found. Starting browser authorization...");
        Console.WriteLine("A browser window will open. Please log in and authorize Modular.");
        Console.WriteLine();

        var ssoClient = new NexusSsoClient(
            settings.Verbose ? CreateLogger<NexusSsoClient>() : null);

        try
        {
            settings.NexusApiKey = await ssoClient.AuthenticateAsync();
            await configService.SaveApiKeyAsync(settings);
            Console.WriteLine("Authorization successful! API key saved to config.");
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("SSO authorization timed out. You can set the API key manually:");
            Console.Error.WriteLine("  export NEXUS_API_KEY=your_key_here");
            Console.Error.WriteLine("  or add 'nexus_api_key' to ~/.config/Modular/config.json");
            throw;
        }
    }

    configService.Validate(settings, requireNexusKey: true);
    // ... rest of initialization
}
```

### Step 5: Add a Dedicated `login` CLI Command

Add a `login` subcommand so users can trigger SSO on demand (re-authenticate, switch accounts):

```csharp
var loginCommand = new Command("login", "Authenticate with NexusMods via browser SSO");
loginCommand.SetHandler(async () =>
{
    var configService = new ConfigurationService();
    var settings = await configService.LoadAsync();

    Console.WriteLine("Opening browser for NexusMods authorization...");
    var ssoClient = new NexusSsoClient(
        settings.Verbose ? CreateLogger<NexusSsoClient>() : null);

    settings.NexusApiKey = await ssoClient.AuthenticateAsync();
    await configService.SaveApiKeyAsync(settings);

    Console.WriteLine("Login successful! API key saved.");
});
rootCommand.AddCommand(loginCommand);
```

---

## File-by-File Changes

| File | Change |
|------|--------|
| `src/Modular.Core/Authentication/NexusSsoClient.cs` | **New file.** SSO WebSocket client (see Step 1) |
| `src/Modular.Core/Configuration/AppSettings.cs` | Add `NexusApplicationSlug` and `NexusSsoEnabled` properties |
| `src/Modular.Core/Configuration/ConfigurationService.cs` | Add `SaveApiKeyAsync()` method |
| `src/Modular.Cli/Program.cs` | Add SSO fallback in `InitializeServices()`, add `login` command |

No changes needed to:
- `NexusModsBackend` - it already reads the API key from `AppSettings.NexusApiKey`
- `FluentHttp` - no protocol changes needed
- `IModBackend` - interface is unaffected

---

## Error Handling

### Reconnection

If the WebSocket disconnects during the flow:
1. Reconnect to `wss://sso.nexusmods.com`
2. Send the handshake with the stored `connection_token` (not null this time)
3. Re-open the browser URL (same UUID)
4. Continue waiting

### Failure Modes

| Scenario | Handling |
|----------|----------|
| WebSocket connection fails | Retry with exponential backoff (3 attempts), then fall back to manual key instructions |
| User closes browser without authorizing | Timeout after 5 minutes, print manual key instructions |
| SSO server returns error | Throw `InvalidOperationException` with error message |
| Network drops mid-flow | Reconnect using `connection_token`, re-send handshake |
| User cancels (Ctrl+C) | `CancellationToken` propagates, WebSocket is disposed via `using` |
| API key is invalid after SSO | `NexusModsBackend` will get 401; user should run `modular login` to re-authenticate |

---

## Testing

### Manual Testing Checklist

1. **Happy path**: Delete API key from config, run Modular, verify browser opens, authorize, verify key is saved
2. **Existing key**: Set API key in config, run Modular, verify SSO is skipped
3. **Environment variable override**: Set `NEXUS_API_KEY` env var, verify SSO is skipped
4. **Timeout**: Start SSO, don't authorize in browser, verify timeout after 5 minutes
5. **Login command**: Run `modular login`, verify re-authorization works
6. **SSO disabled**: Set `nexus_sso_enabled: false`, delete API key, verify Modular falls back to error message
7. **Headless**: Verify `nexus_sso_enabled: false` works in CI without browser

### Unit Testing

The `NexusSsoClient` can be tested by:
- Mocking the WebSocket with a local WebSocket server in tests
- Testing the JSON serialization/deserialization of `SsoHandshake` and `SsoResponse` models
- Testing `OpenBrowser()` isolation (verify correct command per platform)

---

## References

- [NexusMods SSO Integration Demo](https://github.com/Nexus-Mods/sso-integration-demo) - Official reference implementation (JavaScript)
- [NexusMods SSO Demo Source](https://github.com/Nexus-Mods/sso-integration-demo/blob/master/demo.html) - Complete working demo
- [node-nexus-api](https://github.com/Nexus-Mods/node-nexus-api) - Official Node.js client with SSO + OAuth/JWT support
- [NexusMods API Key Page](https://www.nexusmods.com/users/myaccount?tab=api) - Manual API key management (fallback)

### NexusMods API Endpoints Used After Authentication

Once authenticated, the API key is used with these endpoints (already implemented in `NexusModsBackend`):

| Endpoint | Purpose |
|----------|---------|
| `GET /v1/user/tracked_mods.json` | List user's tracked mods |
| `GET /v1/games/{domain}/mods/{id}/files.json` | Get files for a mod |
| `POST /v1/games/{domain}/mods/{id}/files/{fid}/download_link.json` | Get download URL |
| `GET /v1/games/{domain}/mods/{id}.json` | Get mod details |
| `GET /v1/games/{domain}/categories.json` | Get game categories |

### Rate Limits

| Tier | Daily Limit | Hourly Limit | Recovery Rate |
|------|-------------|--------------|---------------|
| Standard | 20,000 | 500 | 1 req/sec |
| Premium | 20,000 | 500 | 1 req/sec (burst: 600) |

Rate limit headers returned on every response:
- `x-rl-daily-limit` / `x-rl-daily-remaining` / `x-rl-daily-reset`
- `x-rl-hourly-limit` / `x-rl-hourly-remaining` / `x-rl-hourly-reset`

Already handled by `NexusRateLimiter` in `Modular.Core.RateLimiting`.
