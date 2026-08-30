using System;
using System.Collections.Generic;
using WindUpKey.Quest;

namespace WindUpKey;

/// <summary>Per-character pairing, role, and doll state (keyed by ContentId in config).</summary>
[Serializable]
public sealed class CharacterProfile
{
    public string PairingKey { get; set; } = string.Empty;

    /// <summary>Last Name@World for local labels only (not used for pairing-key derivation).</summary>
    public string LastKnownIdentity { get; set; } = string.Empty;

    public List<PairedPartner> PairedPartners { get; set; } = [];

    public List<string> PendingPartnerKeys { get; set; } = [];

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

    public bool SafewordEnabled { get; set; }

    public string Safeword { get; set; } = "safeword";

    public double SafewordHours { get; set; } = 1;

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

    /// <summary>Active daily quest difficulty, or <see cref="QuestDifficulty.None"/>.</summary>
    public QuestDifficulty QuestDifficulty { get; set; } = QuestDifficulty.None;

    /// <summary>When the current quest difficulty was accepted (UTC).</summary>
    public DateTimeOffset? QuestAcceptedAtUtc { get; set; }

    /// <summary>Eligible roulette clears credited toward Easy (0–2).</summary>
    public int QuestRouletteClears { get; set; }

    /// <summary>Current-expansion Extreme clears credited toward Medium.</summary>
    public int QuestExtremeClears { get; set; }

    /// <summary>True after a current-tier Savage clear credited toward Hard.</summary>
    public bool QuestSavageCleared { get; set; }

    /// <summary>True after the quest reward has been granted for this accept.</summary>
    public bool QuestRewardClaimed { get; set; }
}
