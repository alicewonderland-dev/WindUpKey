using System;
using System.Collections.Generic;

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

    /// <summary>Outbound key-rotation announces waiting for an online peer.</summary>
    public List<PendingKeyRotation> PendingKeyRotations { get; set; } = [];

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

    public int LowWindWarningsFired { get; set; }

    public double LowWindTriggerHighSeconds { get; set; }

    public double LowWindTriggerMidSeconds { get; set; }

    public double LowWindTriggerLowSeconds { get; set; }

    public DateTimeOffset? LowWindLastWarningUtc { get; set; }
}

/// <summary>Queued <c>keyRotated</c> announce to a partner after a local key change.</summary>
[Serializable]
public sealed class PendingKeyRotation
{
    public string PartnerKey { get; set; } = string.Empty;

    public string OldKey { get; set; } = string.Empty;

    public string NewKey { get; set; } = string.Empty;

    /// <summary>Changer's new Name@World for the partner's local label.</summary>
    public string Identity { get; set; } = string.Empty;
}
