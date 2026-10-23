namespace WindUpKey.Protocol;

/// <summary>
/// Wire protocol version. Bump only on breaking changes. 6: spare springs. The relay refuses winds and unwinds
/// from clients below <see cref="Springs"/>, which would otherwise wind for free and read exact remaining times.
/// </summary>
public static class ProtocolVersion
{
    public const int Current = 6;

    /// <summary>The first version that pays for winds and unwinds with spare springs.</summary>
    public const int Springs = 6;
}

/// <summary>Known message type strings. Clients ignore unknown types.</summary>
public static class MessageTypes
{
    public const string Register = "register";
    public const string Wind = "wind";
    public const string WindResult = "windResult";
    /// <summary>Doll → paired partner: request additional winding, with rounded wind percentage.</summary>
    public const string WindRequest = "windRequest";
    public const string Unwind = "unwind";
    public const string Error = "error";
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string PairSubmit = "pairSubmit";
    public const string PairCancel = "pairCancel";
    public const string PairEstablished = "pairEstablished";
    public const string PairRemove = "pairRemove";
    public const string PresenceQuery = "presenceQuery";
    public const string PresenceResult = "presenceResult";
    public const string OwnerGrant = "ownerGrant";
    public const string OwnerRevoked = "ownerRevoked";
    public const string OwnerSettingsQuery = "ownerSettingsQuery";
    public const string OwnerSettingsResult = "ownerSettingsResult";
    public const string OwnerSettingsUpdate = "ownerSettingsUpdate";
    public const string OwnerSettingsAck = "ownerSettingsAck";
    /// <summary>Owner → doll: request auto-travel near the owner's position (Testing).</summary>
    public const string Call = "call";
    /// <summary>Doll → owner: call accepted or deferred.</summary>
    public const string CallAck = "callAck";
    /// <summary>Doll → owner: call finished (arrived / failed / cancelled).</summary>
    public const string CallResult = "callResult";
    /// <summary>Relay → client: established peers, doll consent, and wind expiry after register.</summary>
    public const string PairSync = "pairSync";
    /// <summary>Client → relay: push doll consent and/or wind expiry (last-write-wins by updatedUtc).</summary>
    public const string DollStatePush = "dollStatePush";
    /// <summary>Relay → sender: offline message accepted into durable outbox.</summary>
    public const string DeliveryQueued = "deliveryQueued";
    /// <summary>Relay → sender: previously queued message handed to the target socket.</summary>
    public const string DeliveryDelivered = "deliveryDelivered";
    /// <summary>Doll → unwinder: what an unwind would cost now (the reply to an unwind with <c>quote</c>).</summary>
    public const string UnwindQuote = "unwindQuote";
    /// <summary>Partner → broken doll: repair them (free).</summary>
    public const string Repair = "repair";
    /// <summary>Client → relay: quest board lines completed, paid in springs.</summary>
    public const string SpringGrant = "springGrant";
    /// <summary>Client → relay → recipient: give springs to a paired player.</summary>
    public const string SpringGift = "springGift";
    /// <summary>Relay → client: the spring balance, after register and after every change.</summary>
    public const string SpringBalance = "springBalance";

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        Register, Wind, WindResult, WindRequest, Unwind, Error, Ping, Pong, PairSubmit, PairCancel, PairEstablished,
        PairRemove, PresenceQuery, PresenceResult, OwnerGrant, OwnerRevoked, OwnerSettingsQuery, OwnerSettingsResult,
        OwnerSettingsUpdate, OwnerSettingsAck, Call, CallAck, CallResult, PairSync, DollStatePush, DeliveryQueued,
        DeliveryDelivered, UnwindQuote, Repair, SpringGrant, SpringGift, SpringBalance,
    };

    /// <summary>True when <paramref name="type"/> is one of the constants above.</summary>
    public static bool IsKnown(string? type) => type is not null && Known.Contains(type);
}
