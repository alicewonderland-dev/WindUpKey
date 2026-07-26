using System.Collections.Generic;
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

/// <summary>Doll asks a paired partner for additional winding.</summary>
public sealed class WindRequestPayload
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Sender pairing key (set/overwritten by relay).</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    /// <summary>Target pairing key.</summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    /// <summary>Rounded percentage of the doll's configured maximum wind, clamped to 0–100.</summary>
    [JsonPropertyName("percentRemaining")]
    public int PercentRemaining { get; set; }
}

/// <summary>Partner-requested clear of remaining wind (doll must have CanUnwindMe or IsOwner for this key).</summary>
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

/// <summary>Relay pushes established peer keys after register (consent stays client-local).</summary>
public sealed class PairSyncPayload
{
    [JsonPropertyName("peerKeys")]
    public List<string> PeerKeys { get; set; } = [];
}

/// <summary>Offline store-and-forward status for a requestId-bearing message.</summary>
public sealed class DeliveryStatusPayload
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
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

/// <summary>Doll designates the recipient as an owner.</summary>
public sealed class OwnerGrantPayload
{
    /// <summary>Doll pairing key (set/overwritten by relay).</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    /// <summary>New owner pairing key.</summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    /// <summary>Optional doll Name@World for the owner's local label.</summary>
    [JsonPropertyName("identity")]
    public string? Identity { get; set; }
}

/// <summary>Doll clears ownership for the recipient (unlock / unpair).</summary>
public sealed class OwnerRevokedPayload
{
    /// <summary>Doll pairing key (set/overwritten by relay).</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    /// <summary>Former owner pairing key.</summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;
}

public sealed class OwnerEmoteInfo
{
    [JsonPropertyName("id")]
    public ushort Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public sealed class OwnerSettingsQueryPayload
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Owner pairing key (set/overwritten by relay).</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    /// <summary>Doll pairing key.</summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;
}

public sealed class OwnerSettingsResultPayload
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Doll pairing key (set/overwritten by relay on outbound).</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    /// <summary>Owner pairing key.</summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    [JsonPropertyName("maxWindHours")]
    public double MaxWindHours { get; set; }

    [JsonPropertyName("autoGroundSit")]
    public bool AutoGroundSit { get; set; }

    [JsonPropertyName("lockEmoteId")]
    public ushort LockEmoteId { get; set; }

    [JsonPropertyName("settingsLocked")]
    public bool SettingsLocked { get; set; }

    [JsonPropertyName("emotes")]
    public List<OwnerEmoteInfo> Emotes { get; set; } = [];

    /// <summary>Doll allows this owner to call (Hardcore forces true on the doll).</summary>
    [JsonPropertyName("canCall")]
    public bool CanCall { get; set; }

    /// <summary>True when Lifestream and vnavmesh IPC are available on the doll (receive-side only; callers ignore this).</summary>
    [JsonPropertyName("travelReady")]
    public bool TravelReady { get; set; }
}

public sealed class OwnerSettingsUpdatePayload
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Owner pairing key (set/overwritten by relay).</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    /// <summary>Doll pairing key.</summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    [JsonPropertyName("maxWindHours")]
    public double? MaxWindHours { get; set; }

    [JsonPropertyName("autoGroundSit")]
    public bool? AutoGroundSit { get; set; }

    [JsonPropertyName("lockEmoteId")]
    public ushort? LockEmoteId { get; set; }

    [JsonPropertyName("settingsLocked")]
    public bool? SettingsLocked { get; set; }
}

public sealed class OwnerSettingsAckPayload
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Doll pairing key (set/overwritten by relay on outbound).</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    /// <summary>Owner pairing key.</summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    [JsonPropertyName("maxWindHours")]
    public double MaxWindHours { get; set; }

    [JsonPropertyName("autoGroundSit")]
    public bool AutoGroundSit { get; set; }

    [JsonPropertyName("lockEmoteId")]
    public ushort LockEmoteId { get; set; }

    [JsonPropertyName("settingsLocked")]
    public bool SettingsLocked { get; set; }
}

/// <summary>Owner requests that the doll travel near the given world position.</summary>
public sealed class CallPayload
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Owner pairing key (set/overwritten by relay).</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    /// <summary>Doll pairing key.</summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    [JsonPropertyName("worldId")]
    public uint WorldId { get; set; }

    [JsonPropertyName("worldName")]
    public string WorldName { get; set; } = string.Empty;

    [JsonPropertyName("territoryId")]
    public uint TerritoryId { get; set; }

    [JsonPropertyName("x")]
    public float X { get; set; }

    [JsonPropertyName("y")]
    public float Y { get; set; }

    [JsonPropertyName("z")]
    public float Z { get; set; }

    /// <summary>
    /// Lifestream <c>ResidentialAetheryteKind</c> as int (Limsa=8, Gridania=2, Ul'dah=9, Foundation=70, Kugane=111).
    /// 0 when the owner is not in a residential district.
    /// </summary>
    [JsonPropertyName("housingCity")]
    public int HousingCity { get; set; }

    /// <summary>1-based ward; 0 when not in housing.</summary>
    [JsonPropertyName("housingWard")]
    public int HousingWard { get; set; }

    /// <summary>1 = main division, 2 = subdivision; 0 when not in housing.</summary>
    [JsonPropertyName("housingDivision")]
    public int HousingDivision { get; set; }

    /// <summary>1-based plot for houses; 0 when unknown, on the street, or apartment.</summary>
    [JsonPropertyName("housingPlot")]
    public int HousingPlot { get; set; }

    /// <summary>1-based apartment room; 0 when not an apartment.</summary>
    [JsonPropertyName("housingApartment")]
    public int HousingApartment { get; set; }

    /// <summary>True when standing in / targeting an apartment wing.</summary>
    [JsonPropertyName("housingIsApartment")]
    public bool HousingIsApartment { get; set; }

    /// <summary>True when inside a house or apartment (outdoor plot coords should be used for travel).</summary>
    [JsonPropertyName("housingIndoor")]
    public bool HousingIndoor { get; set; }
}

public static class CallAckStatuses
{
    public const string Traveling = "traveling";
    public const string Crafting = "crafting";
    public const string Instance = "instance";
    public const string Combat = "combat";
    public const string Busy = "busy";
}

public sealed class CallAckPayload
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Doll pairing key.</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    /// <summary>Owner pairing key.</summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    /// <summary>One of <see cref="CallAckStatuses"/>.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public static class CallResultStatuses
{
    public const string Arrived = "arrived";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public sealed class CallResultPayload
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Doll pairing key.</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    /// <summary>Owner pairing key.</summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    /// <summary>One of <see cref="CallResultStatuses"/>.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
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
    public const string RateLimited = "rate_limited";
    public const string WindRequestCooldown = "wind_request_cooldown";
    public const string CrossDc = "cross_dc";
    public const string TravelUnavailable = "travel_unavailable";
    public const string Busy = "busy";
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
