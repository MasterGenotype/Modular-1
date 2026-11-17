using Modular.Core.Security;
using SteamKit2;
using SteamKit2.Authentication;

namespace Modular.Core.Steam;

/// <summary>
/// One connection to the Steam network through SteamKit2, separate from the Steam client.
/// It uses its own <see cref="SteamUser.LogOnDetails.LoginID"/>, so it does not kick the
/// running Steam client's session off the account.
/// </summary>
public sealed class SteamSession : IAsyncDisposable
{
    /// <summary>"MOD0". Any non-zero value that differs from the Steam client's own session id.</summary>
    private const uint ModularLoginId = 0x4D4F4430;

    private readonly SteamClient _client;
    private readonly CallbackManager _callbacks;
    private readonly SteamUser _user;
    private readonly List<IDisposable> _subscriptions = new();
    private readonly CancellationTokenSource _pumpCts = new();
    private Task? _pump;

    private TaskCompletionSource<bool>? _connected;
    private TaskCompletionSource<SteamUser.LoggedOnCallback>? _loggedOn;
    private readonly TaskCompletionSource _disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SteamSession()
    {
        _client = new SteamClient();
        _callbacks = new CallbackManager(_client);
        _user = _client.GetHandler<SteamUser>()!;
        Apps = _client.GetHandler<SteamApps>()!;
        Content = _client.GetHandler<SteamContent>()!;

        _subscriptions.Add(_callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => _connected?.TrySetResult(true)));
        _subscriptions.Add(_callbacks.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected));
        _subscriptions.Add(_callbacks.Subscribe<SteamUser.LoggedOnCallback>(cb => _loggedOn?.TrySetResult(cb)));
        _subscriptions.Add(_callbacks.Subscribe<SteamUser.LoggedOffCallback>(cb =>
            LoggedOffReason = cb.Result));
    }

    public SteamClient Client => _client;
    public SteamApps Apps { get; }
    public SteamContent Content { get; }
    public bool IsLoggedOn { get; private set; }
    public uint CellId { get; private set; }
    public string? AccountName { get; private set; }
    public SteamID? SteamId => _client.SteamID;
    public EResult? LoggedOffReason { get; private set; }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _pump ??= Task.Run(() => PumpAsync(_pumpCts.Token));
        _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.Connect();
        await _connected.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
    }

    /// <summary>Interactive login by account name and password. Steam Guard is handled by
    /// <paramref name="authenticator"/> (mobile approval or a code).</summary>
    public async Task<AuthPollResult> AuthenticateViaCredentialsAsync(
        string username, string password, IAuthenticator authenticator, CancellationToken ct = default)
    {
        var session = await _client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
        {
            Username = username,
            Password = password,
            DeviceFriendlyName = DeviceName,
            IsPersistentSession = true,
            Authenticator = authenticator,
        });
        return await session.PollingWaitForResultAsync(ct);
    }

    /// <summary>Logs on with a refresh token from an earlier interactive login.</summary>
    public async Task LogOnAsync(string accountName, string refreshToken, CancellationToken ct = default)
    {
        _loggedOn = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _user.LogOn(new SteamUser.LogOnDetails
        {
            Username = accountName,
            AccessToken = refreshToken, // SteamKit2 sends the refresh token in this field
            ShouldRememberPassword = true,
            LoginID = ModularLoginId,
        });
        var result = await _loggedOn.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        if (result.Result != EResult.OK)
            throw new SteamLogOnException(result.Result, result.ExtendedResult);
        IsLoggedOn = true;
        CellId = result.CellID;
        AccountName = accountName;
    }

    /// <summary>Connects and logs on with the stored account, or throws <see cref="SteamNotLoggedInException"/>.</summary>
    public static async Task<SteamSession> OpenStoredAsync(SteamAccountStore accounts, CancellationToken ct = default)
    {
        var stored = await accounts.LoadAsync(ct)
            ?? throw new SteamNotLoggedInException("No Steam login stored. Run 'modular steam login' first.");
        var session = new SteamSession();
        try
        {
            await session.ConnectAsync(ct);
            await session.LogOnAsync(stored.AccountName, stored.RefreshToken, ct);
            return session;
        }
        catch (SteamLogOnException ex) when (ex.Result is EResult.AccessDenied or EResult.Expired or EResult.InvalidPassword)
        {
            await session.DisposeAsync();
            throw new SteamNotLoggedInException(
                $"The stored Steam login was rejected ({ex.Result}). Run 'modular steam login' again.");
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private static string DeviceName => $"Modular ({Environment.MachineName})";

    private void OnDisconnected(SteamClient.DisconnectedCallback cb)
    {
        IsLoggedOn = false;
        _disconnected.TrySetResult();
        var error = new SteamDisconnectedException(LoggedOffReason);
        _connected?.TrySetException(error);
        _loggedOn?.TrySetException(error);
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _callbacks.RunWaitCallbackAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (IsLoggedOn)
        {
            // Steam closes the connection itself after a log-off. Wait for that: calling
            // Disconnect() straight after LogOff() makes SteamKit2 3.5.0 throw from
            // WebSocketConnection.WebSocketContext.Dispose ("RunSynchronously may not be called…").
            _user.LogOff();
            try { await _disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { }
        }
        if (_client.IsConnected)
        {
            try { _client.Disconnect(); }
            catch (InvalidOperationException) { } // same SteamKit2 teardown bug; the socket is going away anyway
        }
        _pumpCts.Cancel();
        if (_pump is not null)
        {
            try { await _pump; } catch (OperationCanceledException) { }
        }
        foreach (var s in _subscriptions)
            s.Dispose();
        _pumpCts.Dispose();
    }
}

/// <summary>The Steam account Modular logs in as: account name plus refresh token, kept in the secret store.</summary>
public sealed class SteamAccountStore(ISecretStore secrets)
{
    private const string AccountKey = "steam.account";
    private const string TokenKey = "steam.refresh-token";

    public ISecretStore Secrets => secrets;

    public async Task<StoredSteamAccount?> LoadAsync(CancellationToken ct = default)
    {
        var account = await secrets.GetAsync(AccountKey, ct);
        var token = await secrets.GetAsync(TokenKey, ct);
        return account is null || token is null ? null : new StoredSteamAccount(account, token);
    }

    public async Task SaveAsync(string accountName, string refreshToken, CancellationToken ct = default)
    {
        await secrets.SetAsync(TokenKey, refreshToken, ct);
        await secrets.SetAsync(AccountKey, accountName, ct);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await secrets.DeleteAsync(TokenKey, ct);
        await secrets.DeleteAsync(AccountKey, ct);
    }
}

public sealed record StoredSteamAccount(string AccountName, string RefreshToken);

public sealed class SteamLogOnException(EResult result, EResult extended)
    : Exception($"Steam log-on failed: {result} ({extended})")
{
    public EResult Result { get; } = result;
}

public sealed class SteamNotLoggedInException(string message) : Exception(message);

public sealed class SteamDisconnectedException(EResult? loggedOffReason)
    : Exception(loggedOffReason is null ? "Disconnected from Steam." : $"Disconnected from Steam ({loggedOffReason}).");
