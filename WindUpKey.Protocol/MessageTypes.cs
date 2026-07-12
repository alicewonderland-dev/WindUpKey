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
    public const string Unwind = "unwind";
    public const string Error = "error";
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string PairSubmit = "pairSubmit";
    public const string PairEstablished = "pairEstablished";
    public const string PairRemove = "pairRemove";
    public const string PresenceQuery = "presenceQuery";
    public const string PresenceResult = "presenceResult";
}
