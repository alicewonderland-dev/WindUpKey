#if WINDUP_TESTING || WINDUP_SELFTEST_UNITS
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WindUpKey.Protocol;

namespace WindUpKey.SelfTest;

/// <summary>Which kind of secret a probe register carries (04-D5).</summary>
public enum ProbeSecretKind
{
    None,
    Malformed,
    FreshRandom,
}

/// <summary>
/// The secret a probe register carries (04-D5): a type-level choice of <see cref="None"/>, <see cref="Malformed"/> (a
/// fixed constant that can never normalise to a valid secret) or <see cref="FreshRandom"/> (generated here, and
/// checked against the character's current and previous secret through a yes/no predicate). There is no way to make
/// one from a string, so the real secret can't be passed to a probe: the only constructor is private and every
/// factory makes its own value.
/// </summary>
public sealed class ProbeSecret
{
    /// <summary>
    /// The malformed secret: '!' isn't base64url and survives <see cref="RelaySecret.Normalize"/>, and it isn't 43
    /// characters, so no relay can read it as a secret (it gets <c>auth_failed</c> in Bind and Enforce).
    /// </summary>
    public const string MalformedValue = "windup-selftest-malformed!";

    public static readonly ProbeSecret None = new(ProbeSecretKind.None, null);

    public static readonly ProbeSecret Malformed = new(ProbeSecretKind.Malformed, MalformedValue);

    private ProbeSecret(ProbeSecretKind kind, string? value)
    {
        Kind = kind;
        WireValue = value;
    }

    public ProbeSecretKind Kind { get; }

    /// <summary>What goes in the register's <c>secret</c> field. Only <see cref="RelayProbe"/> reads it; never log it.</summary>
    internal string? WireValue { get; }

    /// <summary>
    /// A fresh random valid secret that <paramref name="isKnownSecret"/> says is neither the character's current nor its
    /// previous secret (04-D5). Null when every attempt was known (never in practice), so the caller skips the probe.
    /// </summary>
    public static ProbeSecret? FreshRandom(Func<string, bool> isKnownSecret, int attempts = 4)
    {
        for (var i = 0; i < attempts; i++)
        {
            var candidate = RelaySecret.Generate();
            if (!isKnownSecret(candidate))
                return new ProbeSecret(ProbeSecretKind.FreshRandom, candidate);
        }

        return null;
    }

    public override string ToString() => Kind.ToString();
}

/// <summary>A probe connection attempt: the open socket, or why there is none (with the HTTP status when one came back).</summary>
public sealed record ProbeConnection(WebSocket? Socket, int? HttpStatus, string? Failure);

/// <summary>Opens probe WebSockets (04-D5). Each probe socket is its own, never the client's connection.</summary>
public interface IProbeConnector
{
    /// <summary>
    /// Opens a WebSocket to <paramref name="uri"/>, with the shared token header only when <paramref name="withToken"/>.
    /// A refused upgrade or an unreachable host is a result, not an exception; only <paramref name="ct"/> throws.
    /// </summary>
    Task<ProbeConnection> ConnectAsync(Uri uri, bool withToken, TimeSpan timeout, CancellationToken ct);
}

/// <summary>What a <c>/stats</c> request got: the status, the <c>Date</c> header, and <c>accounts</c> when readable.</summary>
public readonly record struct StatsOutcome(int? Status, DateTimeOffset? Date, long? Accounts, string? Failure);

/// <summary>Reads the relay's token-protected <c>/stats</c> (planning §4: read-only).</summary>
public interface IStatsClient
{
    Task<StatsOutcome> GetAsync(Uri uri, bool withToken, TimeSpan timeout, CancellationToken ct);
}

public enum ProbeOutcomeKind
{
    /// <summary>No connection: DNS, TCP or TLS failed, or the upgrade timed out.</summary>
    Unreachable,

    /// <summary>The upgrade got an HTTP status other than 101.</summary>
    HttpRefused,

    /// <summary>The upgrade succeeded (101) and the probe closed it.</summary>
    Upgraded,

    /// <summary>The relay closed the probe (see the status and description).</summary>
    Closed,

    /// <summary>The relay sent <c>pairSync</c>: the register was accepted. The probe closed at once.</summary>
    Accepted,

    AuthFailed,
    AuthRequired,

    /// <summary>The relay sent <c>session_replaced</c> to the probe. The probe closed at once.</summary>
    SessionReplaced,

    /// <summary><c>pair_key_collision</c>: a legacy register for a key held by an open session.</summary>
    Collision,

    RateLimited,

    /// <summary>Another relay error (<see cref="ProbeOutcome.ErrorCode"/>).</summary>
    OtherError,

    /// <summary>A <c>pong</c> to the oversized ping: the relay has no limit before register (older than 03).</summary>
    Pong,

    /// <summary>Nothing decisive within the reply timeout.</summary>
    Timeout,
}

/// <summary>One probe's outcome. Values-free: codes and statuses only.</summary>
public sealed record ProbeOutcome(
    ProbeOutcomeKind Kind,
    int? HttpStatus = null,
    WebSocketCloseStatus? CloseStatus = null,
    string? CloseDescription = null,
    string? ErrorCode = null)
{
    public override string ToString() => Kind switch
    {
        ProbeOutcomeKind.HttpRefused or ProbeOutcomeKind.Upgraded => $"{Kind} (HTTP {HttpStatus?.ToString() ?? "?"})",
        ProbeOutcomeKind.Closed => $"closed {(int?)CloseStatus} \"{CloseDescription}\"",
        ProbeOutcomeKind.OtherError => $"error {ErrorCode}",
        _ => Kind.ToString(),
    };
}

/// <summary>
/// The probe protocol (04-D5) over an injected <see cref="IProbeConnector"/>. Every probe opens its own socket, closes
/// itself (in <c>finally</c>), and never touches the client's connection, so its closes never reach the client's close
/// classification, register rejection or connect policy. At most one probe is open at a time. No Dalamud dependency.
/// </summary>
public sealed class RelayProbe(IProbeConnector connector, SelfTestTimings timings)
{
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    /// <summary>P0: an upgrade with or without the token. 101 is closed at once, before any register.</summary>
    public Task<ProbeOutcome> UpgradeAsync(Uri uri, bool withToken, CancellationToken ct) =>
        WithSocketAsync(uri, withToken, (_, _) => Task.FromResult(new ProbeOutcome(ProbeOutcomeKind.Upgraded, 101)), ct);

    /// <summary>
    /// P1: 4 KiB + 1 before register, as one padded <c>ping</c>. A 03 relay closes 1009 <c>message_too_big</c>; an older
    /// one answers the ping (<see cref="ProbeOutcomeKind.Pong"/>).
    /// </summary>
    public Task<ProbeOutcome> OversizeBeforeRegisterAsync(Uri uri, CancellationToken ct) =>
        WithSocketAsync(uri, true, async (socket, token) =>
        {
            await TrySendAsync(socket, PaddedPing(WireLimits.PreRegisterMessageBytes + 1), WebSocketMessageType.Text, token).ConfigureAwait(false);
            return await ReadDecisiveAsync(socket, null, token).ConfigureAwait(false);
        }, ct);

    /// <summary>P1: a binary message before register. A 03 relay closes 1003 <c>binary_not_supported</c>.</summary>
    public Task<ProbeOutcome> BinaryBeforeRegisterAsync(Uri uri, CancellationToken ct) =>
        WithSocketAsync(uri, true, async (socket, token) =>
        {
            await TrySendAsync(socket, new byte[16], WebSocketMessageType.Binary, token).ConfigureAwait(false);
            return await ReadDecisiveAsync(socket, null, token).ConfigureAwait(false);
        }, ct);

    /// <summary>
    /// P2/P3/P3′: a register for <paramref name="key"/> with <paramref name="secret"/>. Ends at the first decisive answer;
    /// <c>pairSync</c> or <c>session_replaced</c> closes the probe at once. Never without a secret (04-D5: no secretless
    /// register on any key, ever): <see cref="ProbeSecret.None"/> throws before anything is opened.
    /// </summary>
    public Task<ProbeOutcome> RegisterAsync(Uri uri, string key, ProbeSecret secret, CancellationToken ct) =>
        secret.WireValue is null
            ? throw new ArgumentException("A probe never registers without a secret.", nameof(secret))
            : WithSocketAsync(uri, true, async (socket, token) =>
        {
            var register = Envelope.Create(MessageTypes.Register, new RegisterPayload
            {
                PairingKey = WireLimits.NormalizeKey(key),
                Secret = secret.WireValue,
            });
            if (!await TrySendAsync(socket, Encoding.UTF8.GetBytes(register.Serialize()), WebSocketMessageType.Text, token).ConfigureAwait(false)
                && socket.CloseStatus is null)
                return new ProbeOutcome(ProbeOutcomeKind.Unreachable);
            return await ReadDecisiveAsync(socket, register, token).ConfigureAwait(false);
        }, ct);

    /// <summary>A ping padded to exactly <paramref name="bytes"/> UTF-8 bytes (at least the bare ping's size).</summary>
    public static byte[] PaddedPing(int bytes)
    {
        var bare = Encoding.UTF8.GetByteCount(Envelope.Create(MessageTypes.Ping, new { pad = string.Empty }).Serialize());
        var text = Envelope.Create(MessageTypes.Ping, new { pad = new string('x', Math.Max(0, bytes - bare)) }).Serialize();
        return Encoding.UTF8.GetBytes(text);
    }

    private async Task<ProbeOutcome> WithSocketAsync(
        Uri uri,
        bool withToken,
        Func<WebSocket, CancellationToken, Task<ProbeOutcome>> body,
        CancellationToken ct)
    {
        await _oneAtATime.WaitAsync(ct).ConfigureAwait(false);
        WebSocket? socket = null;
        try
        {
            var connection = await connector.ConnectAsync(uri, withToken, timings.ProbeConnect, ct).ConfigureAwait(false);
            if (connection.Socket is null)
            {
                return connection.HttpStatus is { } status and not 101
                    ? new ProbeOutcome(ProbeOutcomeKind.HttpRefused, status)
                    : new ProbeOutcome(ProbeOutcomeKind.Unreachable);
            }

            socket = connection.Socket;
            return await body(socket, ct).ConfigureAwait(false);
        }
        finally
        {
            if (socket is not null)
                await CloseQuietlyAsync(socket).ConfigureAwait(false);
            _oneAtATime.Release();
        }
    }

    /// <summary>Reads until something decides the probe. <paramref name="register"/> is set for register probes.</summary>
    private async Task<ProbeOutcome> ReadDecisiveAsync(WebSocket socket, Envelope? register, CancellationToken ct)
    {
        var reader = new BoundedMessageReader();
        while (true)
        {
            if (socket.State is not (WebSocketState.Open or WebSocketState.CloseSent))
                return socket.CloseStatus is { } closed
                    ? ClassifyClose(closed, socket.CloseStatusDescription)
                    : new ProbeOutcome(ProbeOutcomeKind.Timeout);

            WireMessage message;
            using (var reply = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                reply.CancelAfter(timings.Reply);
                try
                {
                    message = await reader.ReadAsync(socket, WireLimits.RelayMessageBytes, reply.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return new ProbeOutcome(ProbeOutcomeKind.Timeout);
                }
                catch (WebSocketException)
                {
                    return socket.CloseStatus is { } status
                        ? ClassifyClose(status, socket.CloseStatusDescription)
                        : new ProbeOutcome(ProbeOutcomeKind.Unreachable);
                }
            }

            if (message.Kind == WireMessageKind.Close)
                return ClassifyClose(message.CloseStatus, message.CloseDescription);
            if (message.Kind != WireMessageKind.Text || Envelope.TryParse(message.Text!) is not { } envelope)
                continue;

            switch (envelope.Type)
            {
                case MessageTypes.Pong when register is null:
                    return new ProbeOutcome(ProbeOutcomeKind.Pong);
                case MessageTypes.PairSync when register is not null:
                    // Accepted as this key: close at once (04-D5).
                    await AbortNowAsync(socket).ConfigureAwait(false);
                    return new ProbeOutcome(ProbeOutcomeKind.Accepted);
                case MessageTypes.Error when register is not null:
                    var error = SafePayload<ErrorPayload>(envelope);
                    var outcome = ClassifyError(error?.Code);
                    if (outcome.Kind == ProbeOutcomeKind.SessionReplaced)
                        await AbortNowAsync(socket).ConfigureAwait(false);
                    if (outcome.Kind != ProbeOutcomeKind.OtherError || error?.Code is not null)
                        return outcome;
                    continue;
            }
        }
    }

    /// <summary>What a relay error answering a probe register means. Pure.</summary>
    public static ProbeOutcome ClassifyError(string? code) => code switch
    {
        ErrorCodes.AuthFailed => new ProbeOutcome(ProbeOutcomeKind.AuthFailed, ErrorCode: code),
        ErrorCodes.AuthRequired => new ProbeOutcome(ProbeOutcomeKind.AuthRequired, ErrorCode: code),
        ErrorCodes.SessionReplaced => new ProbeOutcome(ProbeOutcomeKind.SessionReplaced, ErrorCode: code),
        ErrorCodes.PairKeyCollision => new ProbeOutcome(ProbeOutcomeKind.Collision, ErrorCode: code),
        ErrorCodes.RateLimited => new ProbeOutcome(ProbeOutcomeKind.RateLimited, ErrorCode: code),
        _ => new ProbeOutcome(ProbeOutcomeKind.OtherError, ErrorCode: code),
    };

    /// <summary>What the relay closing a probe means: the auth refusals by description (01-D5, 01-D12), anything else as is. Pure.</summary>
    public static ProbeOutcome ClassifyClose(WebSocketCloseStatus? status, string? description)
    {
        if (status == WebSocketCloseStatus.PolicyViolation)
        {
            switch (description)
            {
                case ErrorCodes.AuthFailed:
                    return new ProbeOutcome(ProbeOutcomeKind.AuthFailed, CloseStatus: status, CloseDescription: description);
                case ErrorCodes.AuthRequired:
                    return new ProbeOutcome(ProbeOutcomeKind.AuthRequired, CloseStatus: status, CloseDescription: description);
                case ErrorCodes.SessionReplaced:
                    return new ProbeOutcome(ProbeOutcomeKind.SessionReplaced, CloseStatus: status, CloseDescription: description);
            }
        }

        return new ProbeOutcome(ProbeOutcomeKind.Closed, CloseStatus: status, CloseDescription: description);
    }

    private static T? SafePayload<T>(Envelope envelope)
        where T : class
    {
        try
        {
            return envelope.GetPayload<T>();
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static async Task<bool> TrySendAsync(WebSocket socket, byte[] bytes, WebSocketMessageType type, CancellationToken ct)
    {
        try
        {
            await socket.SendAsync(bytes, type, true, ct).ConfigureAwait(false);
            return true;
        }
        catch (WebSocketException)
        {
            // The relay may close mid-send (the limit is crossed); the close is read next.
            return false;
        }
    }

    private static async Task AbortNowAsync(WebSocket socket)
    {
        using var quick = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "selftest", quick.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Aborted below either way.
        }

        socket.Abort();
    }

    private static async Task CloseQuietlyAsync(WebSocket socket)
    {
        using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "selftest", quick.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Disposed below either way.
        }
        finally
        {
            socket.Dispose();
        }
    }
}
#endif
