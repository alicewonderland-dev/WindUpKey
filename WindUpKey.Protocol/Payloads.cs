using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WindUpKey.Protocol;

public sealed class RegisterPayload
{
    /// <summary>
    /// The shared relay token, as every client before 05 sent it. Never sent by new clients (05-D6: null, so it's left out
    /// of the JSON) and never read by any relay: the token is checked only at the HTTP upgrade (the
    /// <c>Authorization</c> header or <c>?token=</c>). Kept so the type still describes what old clients send.
    /// </summary>
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    /// <summary>Sole relay identity. Name@World never registered with the relay.</summary>
    [JsonPropertyName("pairingKey")]
    public string PairingKey { get; set; } = string.Empty;

    /// <summary>
    /// Per-character secret proving ownership of <see cref="PairingKey"/> (see <see cref="RelaySecret"/>).
    /// Omitted by old clients; ignored by old relays. Never log this value.
    /// </summary>
    [JsonPropertyName("secret")]
    public string? Secret { get; set; }

    /// <summary>
    /// The character's role (<see cref="SpringRules.RoleDoll"/> or <see cref="SpringRules.RoleWinder"/>), which sets its
    /// spring cap and grants. Omitted by older clients (treated as a doll) and before a role is chosen.
    /// </summary>
    [JsonPropertyName("role")]
    public string? Role { get; set; }
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

    /// <summary>
    /// The wind's hours. Since protocol 6 the relay sets it from <see cref="Springs"/> (8h each, 12h from the doll's
    /// owner), whatever the sender wrote.
    /// </summary>
    [JsonPropertyName("hours")]
    public double Hours { get; set; }

    /// <summary>Spare springs spent on this wind (protocol 6). The relay takes them from the sender.</summary>
    [JsonPropertyName("springs")]
    public int Springs { get; set; }
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

    /// <summary>
    /// Only ask what it would cost: the doll replies with <see cref="MessageTypes.UnwindQuote"/> and changes nothing.
    /// </summary>
    [JsonPropertyName("quote")]
    public bool Quote { get; set; }

    /// <summary>
    /// Springs paid for the unwind (the quoted cost; 0 from the doll's owner). The relay takes them from the sender, and
    /// the doll refuses it if the cost has gone up since.
    /// </summary>
    [JsonPropertyName("springs")]
    public int Springs { get; set; }
}

/// <summary>Doll → unwinder: what unwinding them costs now.</summary>
public sealed class UnwindQuotePayload
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Doll pairing key (set/overwritten by relay).</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    /// <summary>Unwinder pairing key.</summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    /// <summary>Springs the unwind costs (0 when free: the doll's owner, or nothing to unwind).</summary>
    [JsonPropertyName("springs")]
    public int Springs { get; set; }

    /// <summary>The unwinder is the doll's owner, so it is free.</summary>
    [JsonPropertyName("free")]
    public bool Free { get; set; }
}

/// <summary>Partner → broken doll: repair them. Free; the doll checks Can wind me.</summary>
public sealed class RepairPayload
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Sender pairing key (set/overwritten by relay).</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    /// <summary>Doll pairing key.</summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;
}

/// <summary>Client → relay: quest board lines completed (the relay pays them in springs, within a daily limit).</summary>
public sealed class SpringGrantPayload
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>
    /// Lines completed and not yet paid. The client resends them under the same <see cref="RequestId"/> until the relay
    /// answers, and the relay settles each requestId once.
    /// </summary>
    [JsonPropertyName("lines")]
    public int Lines { get; set; }
}

/// <summary>Client → relay: give springs to a paired player; relay → recipient: springs were given to you.</summary>
public sealed class SpringGiftPayload
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Giver pairing key (set/overwritten by relay).</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    /// <summary>Recipient pairing key.</summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    [JsonPropertyName("springs")]
    public int Springs { get; set; }
}

/// <summary>Relay → client: the spring balance, with why it changed.</summary>
public sealed class SpringBalancePayload
{
    /// <summary>The request this answers, when it answers one (a wind, unwind, gift or grant).</summary>
    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    [JsonPropertyName("balance")]
    public int Balance { get; set; }

    [JsonPropertyName("cap")]
    public int Cap { get; set; }

    /// <summary>The change (positive or negative), 0 for a plain sync.</summary>
    [JsonPropertyName("delta")]
    public int Delta { get; set; }

    /// <summary>One of <see cref="SpringReasons"/>.</summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = SpringReasons.Sync;

    /// <summary>The other player of a gift, wind or refund, when there is one.</summary>
    [JsonPropertyName("peerKey")]
    public string? PeerKey { get; set; }
}

/// <summary>Why a spring balance changed.</summary>
public static class SpringReasons
{
    public const string Sync = "sync";
    public const string Daily = "daily";
    public const string Quest = "quest";
    public const string Wind = "wind";
    public const string Unwind = "unwind";
    public const string Refund = "refund";
    public const string GiftSent = "giftSent";
    public const string GiftReceived = "giftReceived";
}

/// <summary>
/// The doll's wind layers, for multi-PC sync (protocol 6). The relay stores and returns them as they are, under the same
/// last-write-wins stamp as the expiry.
/// </summary>
public sealed class WindLayersWire
{
    /// <summary>Normal wind (0 to max wind hours) at <see cref="AsOfUtc"/>.</summary>
    [JsonPropertyName("normalHours")]
    public double NormalHours { get; set; }

    /// <summary>Quest overwind at <see cref="AsOfUtc"/>.</summary>
    [JsonPropertyName("questHours")]
    public double QuestHours { get; set; }

    /// <summary>Player overwind at <see cref="AsOfUtc"/>.</summary>
    [JsonPropertyName("playerHours")]
    public double PlayerHours { get; set; }

    [JsonPropertyName("asOfUtc")]
    public string? AsOfUtc { get; set; }

    /// <summary>While broken: when the break ends by itself (ISO-8601); null when not broken.</summary>
    [JsonPropertyName("brokenUntilUtc")]
    public string? BrokenUntilUtc { get; set; }
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

    /// <summary>Exact remaining time: sent by protocol 5 dolls only. Protocol 6 dolls leave it 0 (see <see cref="Percent"/>).</summary>
    [JsonPropertyName("remainingSeconds")]
    public double RemainingSeconds { get; set; }

    [JsonPropertyName("remainingDisplay")]
    public string RemainingDisplay { get; set; } = string.Empty;

    /// <summary>Normal wind as a percentage of max wind hours, 0–100 (overwind always reads 100).</summary>
    [JsonPropertyName("percent")]
    public int? Percent { get; set; }

    /// <summary>The doll has player overwind (more winding may break them).</summary>
    [JsonPropertyName("overwound")]
    public bool Overwound { get; set; }

    /// <summary>The doll is broken (this wind broke them, or they were broken already).</summary>
    [JsonPropertyName("broken")]
    public bool Broken { get; set; }

    /// <summary>This wind is what broke them.</summary>
    [JsonPropertyName("brokeNow")]
    public bool BrokeNow { get; set; }

    /// <summary>
    /// For a paid unwind: the springs it actually cost (its cost can fall between the quote and the unwind as wind
    /// drains). The relay keeps that many of the held springs and gives the rest back.
    /// </summary>
    [JsonPropertyName("charged")]
    public int? Charged { get; set; }
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

/// <summary>Cancel only the sender's directed pending pairing request.</summary>
public sealed class PairCancelPayload
{
    [JsonPropertyName("peerKey")]
    public string PeerKey { get; set; } = string.Empty;
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

/// <summary>
/// Relay pushes established peers after register, optionally with doll consent and wind expiry
/// for multi-PC sync. Older clients ignore unknown fields and use peerKeys only.
/// </summary>
public sealed class PairSyncPayload
{
    [JsonPropertyName("peerKeys")]
    public List<string> PeerKeys { get; set; } = [];

    /// <summary>Directed doll consent rows (optional; absent on older relays).</summary>
    [JsonPropertyName("peers")]
    public List<PairConsentSyncEntry>? Peers { get; set; }

    /// <summary>Absolute wind expiry ISO-8601, or null/omitted when unwound. Dolls only.</summary>
    [JsonPropertyName("expiryUtc")]
    public string? ExpiryUtc { get; set; }

    /// <summary>Last-write-wins stamp for ExpiryUtc; absent when no wind row exists yet.</summary>
    [JsonPropertyName("windUpdatedUtc")]
    public string? WindUpdatedUtc { get; set; }

    /// <summary>The wind layers stored with the expiry (protocol 6; absent when the row has none).</summary>
    [JsonPropertyName("layers")]
    public WindLayersWire? Layers { get; set; }
}

/// <summary>One directed consent record from a doll toward a peer.</summary>
public sealed class PairConsentSyncEntry
{
    [JsonPropertyName("peerKey")]
    public string PeerKey { get; set; } = string.Empty;

    [JsonPropertyName("canWindMe")]
    public bool CanWindMe { get; set; }

    [JsonPropertyName("canUnwindMe")]
    public bool CanUnwindMe { get; set; }

    [JsonPropertyName("isOwner")]
    public bool IsOwner { get; set; }

    [JsonPropertyName("canCallMe")]
    public bool CanCallMe { get; set; }

    [JsonPropertyName("updatedUtc")]
    public string UpdatedUtc { get; set; } = string.Empty;
}

/// <summary>Client pushes local doll consent and/or wind expiry for relay LWW merge.</summary>
public sealed class DollStatePushPayload
{
    [JsonPropertyName("peers")]
    public List<PairConsentSyncEntry> Peers { get; set; } = [];

    [JsonPropertyName("expiryUtc")]
    public string? ExpiryUtc { get; set; }

    /// <summary>When set, merge wind; when null/omitted, leave relay wind unchanged.</summary>
    [JsonPropertyName("windUpdatedUtc")]
    public string? WindUpdatedUtc { get; set; }

    /// <summary>True when ExpiryUtc is intentionally cleared (unwound).</summary>
    [JsonPropertyName("clearWind")]
    public bool ClearWind { get; set; }

    /// <summary>The wind layers, merged with the expiry under <see cref="WindUpdatedUtc"/> (protocol 6).</summary>
    [JsonPropertyName("layers")]
    public WindLayersWire? Layers { get; set; }
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

    /// <summary>
    /// Set only by the partner's client when answering a forwarded query (unset until its register is accepted,
    /// 08-D15); the relay's own presence answer, online or offline, omits it.
    /// </summary>
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

    /// <summary>How long a break lasts before the doll recovers by itself (protocol 6; absent from older dolls).</summary>
    [JsonPropertyName("brokenRecoveryMinutes")]
    public int? BrokenRecoveryMinutes { get; set; }
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

    /// <summary>Break recovery minutes (protocol 6); clamped by the doll.</summary>
    [JsonPropertyName("brokenRecoveryMinutes")]
    public int? BrokenRecoveryMinutes { get; set; }
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

    /// <summary>Break recovery minutes now in effect (protocol 6).</summary>
    [JsonPropertyName("brokenRecoveryMinutes")]
    public int? BrokenRecoveryMinutes { get; set; }
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

    /// <summary>
    /// 07-B1 (D4): the doll declined (or stopped) the Call, or her quiet period after a Decline answered it. Only a
    /// new status value, no new field: the relay checks only its length, and older owners ignore it.
    /// </summary>
    public const string Declined = "declined";
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

    /// <summary>
    /// Sender pairing key of a routed error (set/overwritten by relay). Omitted on relay-originated errors;
    /// older relays never set it and older clients ignore it.
    /// </summary>
    [JsonPropertyName("from")]
    public string? From { get; set; }

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
    /// <summary>Relay → client: the register secret does not match the account for this key.</summary>
    public const string AuthFailed = "auth_failed";
    /// <summary>Relay → client: this key has an account, so register must carry its secret.</summary>
    public const string AuthRequired = "auth_required";
    /// <summary>Relay → client: an authenticated session for the same key took over this one.</summary>
    public const string SessionReplaced = "session_replaced";
    /// <summary>
    /// Relay → sender (with <c>requestId</c> and <c>to</c>): the offline partner's message queue is full, so this message
    /// wasn't queued (03-D6). Not relay-only; old clients show their generic failure for it.
    /// </summary>
    public const string QueueFull = "queue_full";
    /// <summary>
    /// Doll → sender (with <c>requestId</c> and <c>to</c>): the doll used her safeword and has paused remote control, so
    /// this unwind, call or owner settings update was refused (06-B1). Peer-sent, so never relay-only: the relay routes it
    /// like any other short code, and old clients show their generic failure for it.
    /// </summary>
    public const string SafewordPaused = "safeword_paused";

    /// <summary>Relay → sender: winding and unwinding need protocol 6 (spare springs); update the plugin.</summary>
    public const string UpdateRequired = "update_required";

    /// <summary>Relay → sender: the doll's plugin is older than protocol 6, so it can't be wound, unwound or repaired yet.</summary>
    public const string PeerUpdateRequired = "peer_update_required";

    /// <summary>Relay → sender: not enough spare springs for this wind, unwind or gift.</summary>
    public const string NotEnoughSprings = "not_enough_springs";

    /// <summary>Relay → sender: the recipient's spare springs are at their cap, so the gift wasn't sent.</summary>
    public const string SpringCapFull = "spring_cap_full";

    /// <summary>Doll → sender: the doll is broken, so the wind was refused (only a repair is accepted).</summary>
    public const string Broken = "broken";

    /// <summary>Doll → sender: the unwind now costs more than was paid; ask for a new quote.</summary>
    public const string CostChanged = "cost_changed";

    /// <summary>Relay → sender: the quest springs for today are used up, or the grant was a replay.</summary>
    public const string GrantRefused = "grant_refused";

    /// <summary>
    /// Session-level codes only the relay may send. The relay drops routed errors with these codes,
    /// and clients ignore them when the error carries a <c>to</c> field (relay-originated errors never do).
    /// </summary>
    public static bool IsRelayOnly(string? code) => code is
        AuthFailed or AuthRequired or SessionReplaced or PairKeyCollision or RateLimited;
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
