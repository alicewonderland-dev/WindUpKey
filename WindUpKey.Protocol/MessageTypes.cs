namespace WindUpKey.Protocol;

/// <summary>Wire protocol version. Bump only on breaking envelope changes.</summary>
public static class ProtocolVersion
{
    public const int Current = 1;
}

/// <summary>Known message type strings. Clients ignore unknown types.</summary>
public static class MessageTypes
{
    public const string Register = "register";
    public const string Wind = "wind";
    public const string WindResult = "windResult";
    public const string Error = "error";
    public const string Ping = "ping";
    public const string Pong = "pong";
}
