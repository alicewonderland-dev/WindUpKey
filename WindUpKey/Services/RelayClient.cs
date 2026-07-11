using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using WindUpKey.Protocol;

namespace WindUpKey.Services;

/// <summary>
/// WebSocket client for the wind-up relay. Wind sources should only call <see cref="SendWindAsync"/>.
/// Game state is sampled on the framework thread via <see cref="Tick"/>; socket IO runs in the background.
/// </summary>
public sealed class RelayClient : IDisposable
{
    private readonly Configuration _config;
    private readonly IClientState _clientState;
    private readonly IObjectTable _objectTable;
    private readonly IPluginLog _log;
    private readonly ConsentService _consent;
    private readonly WindTimerService _timer;
    private readonly IWindNotifier _notifier;

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _reconnectDelayMs = 1000;
    private string? _lastStatus;
    private int _running;

    // Written on framework thread, read by background loop — never touch Dalamud APIs off-thread.
    private volatile bool _loggedIn;
    private volatile string? _cachedIdentity;
    private int _tickLogCounter;

    public RelayClient(
        Configuration config,
        IClientState clientState,
        IObjectTable objectTable,
        IPluginLog log,
        ConsentService consent,
        WindTimerService timer,
        IWindNotifier notifier)
    {
        _config = config;
        _clientState = clientState;
        _objectTable = objectTable;
        _log = log;
        _consent = consent;
        _timer = timer;
        _notifier = notifier;
    }

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    /// <summary>Short offline hint for the config UI. Never includes URL or token.</summary>
    public string? LastStatus => _lastStatus;

    /// <summary>Latest identity sampled on the framework thread.</summary>
    public string? LocalIdentity => _cachedIdentity;

    /// <summary>Call from Framework.Update only — samples login/identity safely.</summary>
    public void Tick()
    {
        try
        {
            _loggedIn = _clientState.IsLoggedIn;
            _cachedIdentity = _loggedIn ? TryReadIdentity() : null;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "WindUpKey Tick: failed reading client state");
            _loggedIn = false;
            _cachedIdentity = null;
            _lastStatus = "Error reading character state (see dalamud.log).";
            return;
        }

        // Occasional heartbeat so we can see Tick is alive while stuck.
        if (Interlocked.Increment(ref _tickLogCounter) % 300 == 1)
        {
            _log.Debug(
                "WindUpKey Tick: running={Running} loggedIn={LoggedIn} identity={Identity} connected={Connected} status={Status}",
                Volatile.Read(ref _running) == 1,
                _loggedIn,
                _cachedIdentity ?? "(none)",
                IsConnected,
                _lastStatus ?? "(null)");
        }
    }

    public void Start()
    {
        _log.Information("WindUpKey relay Start()");
        Stop();

        _lastStatus = "Starting…";
        _reconnectDelayMs = 1000;
        _loopCts = new CancellationTokenSource();
        var ct = _loopCts.Token;
        Interlocked.Exchange(ref _running, 1);

        _loopTask = Task.Run(async () =>
        {
            _log.Debug("WindUpKey relay background loop entered");
            try
            {
                await RunLoopAsync(ct).ConfigureAwait(false);
                _log.Debug("WindUpKey relay background loop exited normally");
            }
            catch (Exception ex)
            {
                _lastStatus = "Connection loop crashed (see dalamud.log).";
                _log.Error(ex, "WindUpKey relay background loop crashed");
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        });
    }

    public void Stop()
    {
        _log.Debug("WindUpKey relay Stop()");
        try
        {
            _loopCts?.Cancel();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "WindUpKey relay Stop cancel");
        }

        // Cancel only — never Wait/Dispose here (framework-thread safe).
        // The background loop exits on cancellation; a new Start() replaces the CTS.
        _loopCts = null;
        _loopTask = null;
        CloseSocket();
        Interlocked.Exchange(ref _running, 0);
        _lastStatus = "Stopped.";
    }

    public async Task SendWindAsync(string targetIdentity, double hours)
    {
        var from = _cachedIdentity;
        if (from is null)
        {
            _notifier.NotifyWinderError("Not logged in.");
            return;
        }

        if (!IsConnected)
        {
            _notifier.NotifyWinderError("Not connected yet. Try again in a moment.");
            return;
        }

        var payload = new WindPayload
        {
            RequestId = Guid.NewGuid().ToString("N"),
            From = from,
            To = PlayerIdentity.Normalize(targetIdentity),
            Hours = hours,
        };

        await SendEnvelopeAsync(Envelope.Create(MessageTypes.Wind, payload), CancellationToken.None);
    }

    private string? TryReadIdentity()
    {
        var player = _objectTable.LocalPlayer;
        if (player is null)
        {
            _log.Verbose("WindUpKey identity: LocalPlayer is null");
            return null;
        }

        string? world = null;
        try
        {
            world = player.HomeWorld.ValueNullable?.Name.ToString();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "WindUpKey identity: HomeWorld read failed");
        }

        if (string.IsNullOrEmpty(world))
        {
            _log.Verbose("WindUpKey identity: world name empty");
            return null;
        }

        var name = player.Name.TextValue;
        if (string.IsNullOrWhiteSpace(name))
        {
            _log.Verbose("WindUpKey identity: name empty");
            return null;
        }

        return PlayerIdentity.Format(name, world);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        _log.Debug("WindUpKey RunLoopAsync begin");
        while (!ct.IsCancellationRequested)
        {
            if (!_loggedIn)
            {
                _lastStatus = "Log into the game to connect.";
                _log.Debug("WindUpKey loop: waiting for login");
                await Task.Delay(1000, ct).ConfigureAwait(false);
                continue;
            }

            var identity = _cachedIdentity;
            if (identity is null)
            {
                _lastStatus = "Waiting for character data…";
                _log.Debug("WindUpKey loop: waiting for character identity");
                await Task.Delay(1000, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                _lastStatus = "Connecting…";
                _log.Information("WindUpKey loop: connecting as {Identity}", identity);
                await ConnectAndRegisterAsync(identity, ct).ConfigureAwait(false);
                _lastStatus = null;
                _reconnectDelayMs = 1000;
                _log.Information("WindUpKey loop: connected, entering receive loop");
                await ReceiveLoopAsync(ct).ConfigureAwait(false);
                _lastStatus = "Disconnected. Reconnecting…";
                _log.Information("WindUpKey loop: receive loop ended");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _log.Debug("WindUpKey loop: cancelled");
                break;
            }
            catch (Exception ex)
            {
                _lastStatus = "Cannot reach the host. Is Wind-Up Key Host running?";
                _log.Warning(ex, "WindUpKey relay connection issue");
            }

            CloseSocket();
            try
            {
                _log.Debug("WindUpKey loop: reconnect delay {DelayMs}ms", _reconnectDelayMs);
                await Task.Delay(_reconnectDelayMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _reconnectDelayMs = Math.Min(_reconnectDelayMs * 2, 30_000);
        }

        _log.Debug("WindUpKey RunLoopAsync end");
    }

    private async Task ConnectAndRegisterAsync(string identity, CancellationToken ct)
    {
        CloseSocket();
        _config.ApplyRelayDefaults();

        Exception? lastError = null;
        foreach (var baseUrl in CandidateUrls())
        {
            var safeHost = SafeHost(baseUrl);
            try
            {
                _lastStatus = $"Connecting ({safeHost})…";
                _log.Information("WindUpKey connect attempt host={Host}", safeHost);
                var uri = BuildUri(baseUrl);
                _socket = new ClientWebSocket();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(baseUrl.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
                    ? TimeSpan.FromSeconds(2)
                    : TimeSpan.FromSeconds(15));
                await _socket.ConnectAsync(uri, timeoutCts.Token).ConfigureAwait(false);
                _log.Information("WindUpKey WebSocket open host={Host} state={State}", safeHost, _socket.State);
                lastError = null;
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                lastError = ex;
                _log.Warning(ex, "WindUpKey connect failed host={Host}", safeHost);
                CloseSocket();
            }
        }

        if (_socket is not { State: WebSocketState.Open })
            throw lastError ?? new InvalidOperationException("Unable to open WebSocket");

        var register = new RegisterPayload
        {
            Identity = identity,
            Token = string.IsNullOrEmpty(_config.RelayToken) ? null : _config.RelayToken,
        };
        await SendEnvelopeAsync(Envelope.Create(MessageTypes.Register, register), ct).ConfigureAwait(false);
        _log.Information("WindUpKey registered on relay as {Identity}", identity);
    }

    private static IEnumerable<string> CandidateUrls()
    {
        yield return RelayDefaults.LocalRelayUrl;
        yield return RelayDefaults.RelayUrl;
    }

    private static string SafeHost(string baseUrl)
    {
        try
        {
            var uri = new Uri(baseUrl);
            return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
        }
        catch
        {
            return "(invalid-url)";
        }
    }

    private Uri BuildUri(string baseUrl)
    {
        var url = baseUrl.Trim();
        if (!string.IsNullOrEmpty(_config.RelayToken))
        {
            var sep = url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            url = $"{url}{sep}token={Uri.EscapeDataString(_config.RelayToken)}";
        }

        return new Uri(url);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (_socket is { State: WebSocketState.Open } && !ct.IsCancellationRequested)
        {
            var result = await _socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                _log.Information("WindUpKey socket closed by peer: {Status}", result.CloseStatus?.ToString() ?? "(none)");
                break;
            }

            if (result.MessageType != WebSocketMessageType.Text)
                continue;

            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            while (!result.EndOfMessage)
            {
                result = await _socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                json += Encoding.UTF8.GetString(buffer, 0, result.Count);
            }

            await HandleMessageAsync(json, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleMessageAsync(string json, CancellationToken ct)
    {
        var envelope = Envelope.TryParse(json);
        if (envelope is null)
            return;

        switch (envelope.Type)
        {
            case MessageTypes.Wind:
                await HandleInboundWindAsync(envelope, ct).ConfigureAwait(false);
                break;
            case MessageTypes.WindResult:
                HandleWindResult(envelope);
                break;
            case MessageTypes.Error:
                HandleError(envelope);
                break;
            case MessageTypes.Ping:
                await SendEnvelopeAsync(Envelope.Create(MessageTypes.Pong, new { }), ct).ConfigureAwait(false);
                break;
            case MessageTypes.Pong:
                break;
            default:
                break;
        }
    }

    private async Task HandleInboundWindAsync(Envelope envelope, CancellationToken ct)
    {
        var payload = envelope.GetPayload<WindPayload>();
        if (payload is null)
            return;

        if (!_config.IsDoll)
        {
            await SendEnvelopeAsync(Envelope.Create(MessageTypes.Error, new ErrorPayload
            {
                RequestId = payload.RequestId,
                To = payload.From,
                Code = ErrorCodes.NotAllowed,
                Message = "Target is not in Doll mode.",
            }), ct).ConfigureAwait(false);
            return;
        }

        if (!_consent.IsAllowed(payload.From))
        {
            await SendEnvelopeAsync(Envelope.Create(MessageTypes.Error, new ErrorPayload
            {
                RequestId = payload.RequestId,
                To = payload.From,
                Code = ErrorCodes.NotAllowed,
                Message = "Target is not accepting winds from you (whitelist).",
            }), ct).ConfigureAwait(false);
            return;
        }

        var remaining = _timer.AddHours(payload.Hours);
        await SendEnvelopeAsync(Envelope.Create(MessageTypes.WindResult, new WindResultPayload
        {
            RequestId = payload.RequestId,
            From = _cachedIdentity ?? string.Empty,
            To = payload.From,
            RemainingSeconds = remaining.TotalSeconds,
            RemainingDisplay = WindTimerService.FormatRemaining(remaining),
        }), ct).ConfigureAwait(false);
    }

    private void HandleWindResult(Envelope envelope)
    {
        var payload = envelope.GetPayload<WindResultPayload>();
        if (payload is null)
            return;

        var remaining = TimeSpan.FromSeconds(Math.Max(0, payload.RemainingSeconds));
        var display = string.IsNullOrEmpty(payload.RemainingDisplay)
            ? WindTimerService.FormatRemaining(remaining)
            : payload.RemainingDisplay;
        _notifier.NotifyWinderRemaining(payload.From, remaining);
        _ = display;
    }

    private void HandleError(Envelope envelope)
    {
        var payload = envelope.GetPayload<ErrorPayload>();
        if (payload is null)
            return;
        _notifier.NotifyWinderError(string.IsNullOrEmpty(payload.Message) ? payload.Code : payload.Message);
    }

    private async Task SendEnvelopeAsync(Envelope envelope, CancellationToken ct)
    {
        if (_socket is not { State: WebSocketState.Open })
            return;

        var bytes = Encoding.UTF8.GetBytes(envelope.Serialize());
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void CloseSocket()
    {
        try
        {
            _socket?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "WindUpKey CloseSocket");
        }

        _socket = null;
    }

    public void Dispose()
    {
        Stop();
        _sendLock.Dispose();
    }
}
