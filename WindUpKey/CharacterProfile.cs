using System;
using System.Collections.Generic;
using WindUpKey.Quest;

namespace WindUpKey;

/// <summary>Per-character pairing, role, and doll state (keyed by ContentId in config).</summary>
[Serializable]
public sealed class CharacterProfile
{
    /// <summary>
    /// Emote sheet row for Sit on Ground (<c>/groundsit</c>): the default and fallback unwound emote. 06-A13: the one
    /// named constant for it (not serialized).
    /// </summary>
    public const ushort GroundSitEmoteId = 52;

    public string PairingKey { get; set; } = string.Empty;

    /// <summary>Last Name@World for local labels only (not used for pairing-key derivation).</summary>
    public string LastKnownIdentity { get; set; } = string.Empty;

    public List<PairedPartner> PairedPartners { get; set; } = [];

    public List<string> PendingPartnerKeys { get; set; } = [];

    /// <summary>Unpairs not yet sent to the relay (made offline, or the send failed); sent on the next connect.</summary>
    public List<PendingPairRemoval> PendingPairRemovals { get; set; } = [];

    /// <summary>Dolls this character owns (from remote ownerGrant).</summary>
    public List<OwnedDoll> OwnedDolls { get; set; } = [];

    public PlayerRole Role { get; set; } = PlayerRole.Unset;

    public bool HasCompletedInitialSetup { get; set; }

    public bool HardcoreMode { get; set; }

    /// <summary>When Hardcore was last successfully cleared via /windup unlock.</summary>
    public DateTimeOffset? HardcoreLastClearedUtc { get; set; }

    public bool DebugMode { get; set; }

    public double MaxWindHours { get; set; } = 72;

    /// <summary>When true, only owners may change max hours / unwound emote settings.</summary>
    public bool OwnerSettingsLocked { get; set; }

    /// <summary>
    /// 06-A1: this character's "play emote when unwound" (mirrored in <see cref="Configuration.AutoGroundSit"/>).
    /// Null means not migrated yet: <see cref="ProfileMigration.FillLockEmote"/> gives it the global value.
    /// </summary>
    public bool? AutoGroundSit { get; set; }

    /// <summary>
    /// 06-A1: this character's unwound emote row (mirrored in <see cref="Configuration.LockEmoteId"/>). Null means not
    /// migrated yet, as for <see cref="AutoGroundSit"/>.
    /// </summary>
    public ushort? LockEmoteId { get; set; }

    public bool SafewordEnabled { get; set; }

    public string Safeword { get; set; } = "safeword";

    public double SafewordHours { get; set; } = 1;

    /// <summary>
    /// 06-B1: until when the safeword pauses remote control (owners' unwind, call and settings updates are refused), or
    /// null for no pause. Read through <see cref="Services.SafewordPause"/>. Older builds ignore it (and drop it on save).
    /// </summary>
    public DateTimeOffset? SafewordPausedUntilUtc { get; set; }

    public DateTimeOffset? ExpiryUtc { get; set; }

    /// <summary>Last-write-wins stamp for <see cref="ExpiryUtc"/> sync with the relay.</summary>
    public DateTimeOffset? WindUpdatedUtc { get; set; }

    /// <summary>Last paired-partner wind request sent by this doll; shared across all partners.</summary>
    public DateTimeOffset? LastWindRequestUtc { get; set; }

    public int LowWindWarningsFired { get; set; }

    public double LowWindTriggerHighSeconds { get; set; }

    public double LowWindTriggerMidSeconds { get; set; }

    public double LowWindTriggerLowSeconds { get; set; }

    public DateTimeOffset? LowWindLastWarningUtc { get; set; }

    /// <summary>
    /// The doll's quest board (stickers, this reset's slots, the weekly rerolls). Config version 13 replaced the daily
    /// quest fields with it; their values are dropped when loaded (an older build drops this when it saves).
    /// </summary>
    public QuestBoardState QuestBoard { get; set; } = new();

    /// <summary>Achievement flags (<see cref="AchievementFlags"/>) and when each was first earned.</summary>
    public Dictionary<string, DateTimeOffset> Achievements { get; set; } = new(StringComparer.Ordinal);
}
