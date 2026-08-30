namespace WindUpKey.Protocol;

/// <summary>Wire protocol version. Bump only on breaking envelope changes.</summary>
public static class ProtocolVersion
{
    public const int Current = 5;
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
}
