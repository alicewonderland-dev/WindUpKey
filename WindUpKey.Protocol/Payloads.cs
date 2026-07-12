using System.Text.Json.Serialization;

namespace WindUpKey.Protocol;

public sealed class RegisterPayload
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    /// <summary>Sole relay identity. Name@World never registered with the relay.</summary>
    [JsonPropertyName("pairingKey")]
    public string PairingKey { get; set; } = string.Empty;
}

public sealed class WindPayload
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Sender pairing key (set/overwritten by relay).</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    /// <summary>Target pairing key.</summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    [JsonPropertyName("hours")]
    public double Hours { get; set; }
}

/// <summary>Partner-requested clear of remaining wind (doll must have CanUnwindMe for this key).</summary>
public sealed class UnwindPayload
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Sender pairing key (set/overwritten by relay).</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    /// <summary>Target pairing key.</summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;
}

public sealed class WindResultPayload
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Responder pairing key.</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    /// <summary>Original winder pairing key.</summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    [JsonPropertyName("remainingSeconds")]
    public double RemainingSeconds { get; set; }

    [JsonPropertyName("remainingDisplay")]
    public string RemainingDisplay { get; set; } = string.Empty;
}

public sealed class PairSubmitPayload
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("myKey")]
    public string MyKey { get; set; } = string.Empty;

    [JsonPropertyName("theirKey")]
    public string TheirKey { get; set; } = string.Empty;
}

public sealed class PairEstablishedPayload
{
    [JsonPropertyName("peerKey")]
    public string PeerKey { get; set; } = string.Empty;
}

public sealed class PairRemovePayload
{
    [JsonPropertyName("peerKey")]
    public string PeerKey { get; set; } = string.Empty;
}

public sealed class PresenceQueryPayload
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Sender pairing key (set/overwritten by relay).</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    /// <summary>Target pairing key.</summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;
}

public sealed class PresenceResultPayload
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Responder or relay-attributed pairing key.</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    /// <summary>Original requester pairing key.</summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    [JsonPropertyName("online")]
    public bool Online { get; set; }

    /// <summary>Set by peer when answering a forwarded query; omit on relay offline replies.</summary>
    [JsonPropertyName("stillPaired")]
    public bool? StillPaired { get; set; }
}

public sealed class ErrorPayload
{
    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    /// <summary>When set, relay routes this error to the given pairing key.</summary>
    [JsonPropertyName("to")]
    public string? To { get; set; }

    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public static class ErrorCodes
{
    public const string TargetOffline = "target_offline";
    public const string NotAllowed = "not_allowed";
    public const string Unauthorized = "unauthorized";
    public const string BadRequest = "bad_request";
    public const string NotRegistered = "not_registered";
    public const string PairFailed = "pair_failed";
    public const string PairKeyCollision = "pair_key_collision";
}

/// <summary>Helpers for Name@World identity strings (clientside only).</summary>
public static class PlayerIdentity
{
    public static string Format(string name, string world) => $"{name.Trim()}@{world.Trim()}";

    public static bool TryParse(string identity, out string name, out string world)
    {
        name = string.Empty;
        world = string.Empty;
        var at = identity.LastIndexOf('@');
        if (at <= 0 || at >= identity.Length - 1)
            return false;
        name = identity[..at].Trim();
        world = identity[(at + 1)..].Trim();
        return name.Length > 0 && world.Length > 0;
    }

    public static string Normalize(string identity) => identity.Trim();
}
