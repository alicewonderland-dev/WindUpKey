using System.Text.Json;
using System.Text.Json.Nodes;

namespace WindUpKey.Protocol;

/// <summary>
/// Every size and resource limit of the wire protocol, shared by relay, client and tests so they use the same numbers.
/// Message limits count UTF-8 bytes per WebSocket message (all frames up to <c>EndOfMessage</c>). Field limits count
/// UTF-16 code units after JSON unescaping.
/// </summary>
public static class WireLimits
{
    /// <summary>The largest message the relay reads from a session once it has registered (03-D2).</summary>
    public const int RelayMessageBytes = 64 * 1024;

    /// <summary>The largest message the relay reads from a connection that hasn't registered yet (03-D2).</summary>
    public const int PreRegisterMessageBytes = 4 * 1024;

    /// <summary>The most messages a connection may send before it registers, pings included (03-D3).</summary>
    public const int PreRegisterMessages = 8;

    /// <summary>How long a connection has to register before the relay closes it (03-D3).</summary>
    public static readonly TimeSpan RegisterTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The largest envelope the relay sends that carries client data (a forward, a rebuilt reply, a queued row), after
    /// re-serialisation (03-D5). Relay-originated messages (pairSync, delivery status, relay errors) are exempt.
    /// </summary>
    public const int RoutedMessageBytes = 64 * 1024;

    /// <summary>The largest outbox row (03-D6).</summary>
    public const int OutboxRowBytes = 4 * 1024;

    /// <summary>The most pending, quota-counted outbox rows from one sender to one recipient (03-D6).</summary>
    public const int OutboxPerSender = 100;

    /// <summary>The most pending, quota-counted outbox rows for one recipient (03-D6).</summary>
    public const int OutboxPerRecipient = 1000;

    /// <summary>
    /// How often both ends ping an idle connection (05-D3): the relay's default for <c>Relay:KeepAliveIntervalSeconds</c>,
    /// and the client's <c>ClientWebSocket</c> setting.
    /// </summary>
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long either end waits for the pong before dropping the connection as dead (05-D3): the relay's default for
    /// <c>Relay:KeepAliveTimeoutSeconds</c>, and the client's setting. Long enough for a long game hitch, since a client
    /// answers pings only while a receive is pending.
    /// </summary>
    public static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(90);

    /// <summary>The largest message the client reads; anything bigger is discarded and the connection kept (03-D10).</summary>
    public const int ClientMessageBytes = 1024 * 1024;

    /// <summary>The largest message the client sends, so a new client never trips the relay's limit (03-D13).</summary>
    public const int ClientSendBytes = RelayMessageBytes;

    /// <summary>
    /// <c>requestId</c>, <c>code</c>, <c>status</c>, timestamp strings, <c>identity</c>, <c>worldName</c>, emote
    /// <c>name</c> and <c>remainingDisplay</c> (03-D4).
    /// </summary>
    public const int ShortField = 64;

    /// <summary><c>message</c> (03-D4).</summary>
    public const int MessageField = 256;

    /// <summary><c>emotes</c> and <c>dollStatePush.peers</c> (03-D4).</summary>
    public const int ListEntries = 512;

    /// <summary>The length every key field must normalise to (03-D4).</summary>
    public const int KeyLength = 8;

    /// <summary>
    /// A pairing key as the relay compares it: trimmed, upper-cased (invariant), keeping only ASCII letters and digits.
    /// Empty for null or blank input.
    /// </summary>
    public static string NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return string.Empty;
        var chars = key.Trim().ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).ToArray();
        return new string(chars);
    }

    /// <summary>True when <paramref name="key"/> normalises to exactly <see cref="KeyLength"/> characters.</summary>
    public static bool IsKey(string? key) => NormalizeKey(key).Length == KeyLength;

    /// <summary>
    /// A <c>from</c> as a client writes it: absent, or a key. The relay overwrites it, but never accepts an oversized one.
    /// </summary>
    public static bool IsOptionalKey(string? key) => string.IsNullOrEmpty(key) || IsKey(key);

    /// <summary>Absent, or at most <see cref="ShortField"/> code units.</summary>
    public static bool FitsShort(string? value) => value is null || value.Length <= ShortField;

    /// <summary>Absent, or at most <see cref="MessageField"/> code units.</summary>
    public static bool FitsMessage(string? value) => value is null || value.Length <= MessageField;

    public static bool IsValid(WindPayload p) => FitsShort(p.RequestId) && IsOptionalKey(p.From) && IsKey(p.To);

    public static bool IsValid(WindRequestPayload p) => FitsShort(p.RequestId) && IsOptionalKey(p.From) && IsKey(p.To);

    public static bool IsValid(UnwindPayload p) => FitsShort(p.RequestId) && IsOptionalKey(p.From) && IsKey(p.To);

    public static bool IsValid(WindResultPayload p) =>
        FitsShort(p.RequestId) && IsOptionalKey(p.From) && IsKey(p.To) && FitsShort(p.RemainingDisplay);

    public static bool IsValid(PairSubmitPayload p) => FitsShort(p.RequestId) && IsKey(p.MyKey) && IsKey(p.TheirKey);

    public static bool IsValid(PairCancelPayload p) => IsKey(p.PeerKey);

    public static bool IsValid(PairRemovePayload p) => IsKey(p.PeerKey);

    /// <summary>At most <see cref="ListEntries"/> consent rows, each for a key, with short stamps.</summary>
    public static bool IsValid(DollStatePushPayload p) =>
        FitsShort(p.ExpiryUtc)
        && FitsShort(p.WindUpdatedUtc)
        && (p.Peers is null
            || (p.Peers.Count <= ListEntries
                && p.Peers.All(e => e is not null && IsKey(e.PeerKey) && FitsShort(e.UpdatedUtc))));

    public static bool IsValid(PresenceQueryPayload p) => FitsShort(p.RequestId) && IsOptionalKey(p.From) && IsKey(p.To);

    public static bool IsValid(PresenceResultPayload p) => FitsShort(p.RequestId) && IsOptionalKey(p.From) && IsKey(p.To);

    public static bool IsValid(OwnerGrantPayload p) => IsOptionalKey(p.From) && IsKey(p.To) && FitsShort(p.Identity);

    public static bool IsValid(OwnerRevokedPayload p) => IsOptionalKey(p.From) && IsKey(p.To);

    public static bool IsValid(OwnerSettingsQueryPayload p) => FitsShort(p.RequestId) && IsOptionalKey(p.From) && IsKey(p.To);

    /// <summary>At most <see cref="ListEntries"/> emotes, each with a short name.</summary>
    public static bool IsValid(OwnerSettingsResultPayload p) =>
        FitsShort(p.RequestId)
        && IsOptionalKey(p.From)
        && IsKey(p.To)
        && (p.Emotes is null
            || (p.Emotes.Count <= ListEntries && p.Emotes.All(e => e is not null && FitsShort(e.Name))));

    public static bool IsValid(OwnerSettingsUpdatePayload p) => FitsShort(p.RequestId) && IsOptionalKey(p.From) && IsKey(p.To);

    public static bool IsValid(OwnerSettingsAckPayload p) => FitsShort(p.RequestId) && IsOptionalKey(p.From) && IsKey(p.To);

    public static bool IsValid(CallPayload p) =>
        FitsShort(p.RequestId) && IsOptionalKey(p.From) && IsKey(p.To) && FitsShort(p.WorldName);

    public static bool IsValid(CallAckPayload p) =>
        FitsShort(p.RequestId) && IsOptionalKey(p.From) && IsKey(p.To) && FitsShort(p.Status) && FitsMessage(p.Message);

    public static bool IsValid(CallResultPayload p) =>
        FitsShort(p.RequestId) && IsOptionalKey(p.From) && IsKey(p.To) && FitsShort(p.Status) && FitsMessage(p.Message);

    /// <summary>A routed error: addressed to a key, with a short code and a bounded message.</summary>
    public static bool IsValid(ErrorPayload p) =>
        FitsShort(p.RequestId) && IsOptionalKey(p.From) && IsKey(p.To) && FitsShort(p.Code) && FitsMessage(p.Message);

    /// <summary>
    /// Whether a client's payload of <paramref name="type"/> is within the field limits (03-D4). Types the relay doesn't
    /// forward, route, queue or store (register, ping, pong, unknown ones) and absent payloads pass: their handlers
    /// deal with them. Throws <see cref="JsonException"/> when the payload doesn't deserialise.
    /// </summary>
    public static bool IsValidPayload(string type, JsonObject? payload)
    {
        if (payload is null)
            return true;

        return type switch
        {
            MessageTypes.Wind => Check<WindPayload>(payload, IsValid),
            MessageTypes.WindRequest => Check<WindRequestPayload>(payload, IsValid),
            MessageTypes.Unwind => Check<UnwindPayload>(payload, IsValid),
            MessageTypes.WindResult => Check<WindResultPayload>(payload, IsValid),
            MessageTypes.PairSubmit => Check<PairSubmitPayload>(payload, IsValid),
            MessageTypes.PairCancel => Check<PairCancelPayload>(payload, IsValid),
            MessageTypes.PairRemove => Check<PairRemovePayload>(payload, IsValid),
            MessageTypes.DollStatePush => Check<DollStatePushPayload>(payload, IsValid),
            MessageTypes.PresenceQuery => Check<PresenceQueryPayload>(payload, IsValid),
            MessageTypes.PresenceResult => Check<PresenceResultPayload>(payload, IsValid),
            MessageTypes.OwnerGrant => Check<OwnerGrantPayload>(payload, IsValid),
            MessageTypes.OwnerRevoked => Check<OwnerRevokedPayload>(payload, IsValid),
            MessageTypes.OwnerSettingsQuery => Check<OwnerSettingsQueryPayload>(payload, IsValid),
            MessageTypes.OwnerSettingsResult => Check<OwnerSettingsResultPayload>(payload, IsValid),
            MessageTypes.OwnerSettingsUpdate => Check<OwnerSettingsUpdatePayload>(payload, IsValid),
            MessageTypes.OwnerSettingsAck => Check<OwnerSettingsAckPayload>(payload, IsValid),
            MessageTypes.Call => Check<CallPayload>(payload, IsValid),
            MessageTypes.CallAck => Check<CallAckPayload>(payload, IsValid),
            MessageTypes.CallResult => Check<CallResultPayload>(payload, IsValid),
            MessageTypes.Error => Check<ErrorPayload>(payload, IsValid),
            _ => true,
        };
    }

    private static bool Check<T>(JsonObject payload, Func<T, bool> isValid)
        where T : class =>
        payload.Deserialize<T>(Envelope.JsonOptions) is not { } typed || isValid(typed);
}

/// <summary>Close descriptions the relay uses for its 03 limits. None of them is an auth signal on the client (03-D14).</summary>
public static class WireCloseReasons
{
    /// <summary>Close 1009: a message over the relay's limit (03-D2).</summary>
    public const string MessageTooBig = "message_too_big";

    /// <summary>Close 1003: a binary message (03-D2).</summary>
    public const string BinaryNotSupported = "binary_not_supported";

    /// <summary>Close 1008: no register within <see cref="WireLimits.RegisterTimeout"/> (03-D3).</summary>
    public const string RegisterTimeout = "register_timeout";

    /// <summary>Close 1008: more than <see cref="WireLimits.PreRegisterMessages"/> messages before register (03-D3).</summary>
    public const string RegisterRequired = "register_required";

    /// <summary>
    /// Close 1013 (try again later): this client address created too many accounts recently, so a register that would
    /// create one is refused (05-D2). Never an auth signal: the client reconnects on its normal backoff.
    /// </summary>
    public const string BindRateLimited = "bind_rate_limited";
}
