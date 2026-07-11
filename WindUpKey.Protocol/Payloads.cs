using System.Text.Json.Serialization;

namespace WindUpKey.Protocol;

public sealed class RegisterPayload
{
    [JsonPropertyName("identity")]
    public string Identity { get; set; } = string.Empty;

    [JsonPropertyName("token")]
    public string? Token { get; set; }
}

public sealed class WindPayload
{
    /// <summary>Correlation id so windResult/error can be matched by the requester.</summary>
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    [JsonPropertyName("hours")]
    public double Hours { get; set; }
}

public sealed class WindResultPayload
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    /// <summary>Remaining seconds after the wind (winder-facing only).</summary>
    [JsonPropertyName("remainingSeconds")]
    public double RemainingSeconds { get; set; }

    [JsonPropertyName("remainingDisplay")]
    public string RemainingDisplay { get; set; } = string.Empty;
}

public sealed class ErrorPayload
{
    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    /// <summary>When set, relay routes this error to the given identity (e.g. the winder).</summary>
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
}

/// <summary>Helpers for Name@World identity strings.</summary>
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
