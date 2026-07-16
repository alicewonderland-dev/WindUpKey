using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Configuration;
using WindUpKey.Protocol;

namespace WindUpKey;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 7;

    /// <summary>Orphan profile from pre-v6 flat config until the first logged-in ContentId claims it.</summary>
    public const string PendingProfileKey = "pending";

    public int Version { get; set; } = CurrentVersion;

    public string RelayUrl { get; set; } = RelayDefaults.RelayUrl;

    /// <summary>Shared relay token. Never log this value.</summary>
    public string RelayToken { get; set; } = RelayDefaults.RelayToken;

    /// <summary>Per-character state keyed by ContentId hex. Active fields below mirror the active entry.</summary>
    public Dictionary<string, CharacterProfile> Profiles { get; set; } = new(StringComparer.Ordinal);

    /// <summary>ContentId hex of the profile currently loaded into the flat active fields.</summary>
    public string ActiveContentId { get; set; } = string.Empty;

    /// <summary>Unset until first-launch role prompt.</summary>
    public PlayerRole Role { get; set; } = PlayerRole.Unset;

    /// <summary>True after the first Doll/Winder choice. Starter wind is only granted before this.</summary>
    public bool HasCompletedInitialSetup { get; set; }

    /// <summary>When true, role is locked to Doll and cannot switch to Winder.</summary>
    public bool HardcoreMode { get; set; }

    /// <summary>When Hardcore was last successfully cleared via /windup unlock (per character).</summary>
    public DateTimeOffset? HardcoreLastClearedUtc { get; set; }

    /// <summary>
    /// Pairing keys that may enable debug/testing features
    /// (Alice Selena@Sargatanas, Scotti Pixie@Sargatanas).
    /// </summary>
    public static readonly string[] DebugOwnerPairingKeys = ["WZ9T4UEC", "DDHJMLL0"];

    /// <summary>When true, unlocks debug/testing features (self-wind, unwind UI, /windup check, /windup debug).</summary>
    public bool DebugMode { get; set; }

    /// <summary>True when the local pairing key is a debug owner.</summary>
    public bool IsDebugOwner => IsDebugOwnerKey(PairingKey);

    /// <summary>Debug features require both the toggle and an owner pairing key.</summary>
    public bool IsDebugEnabled => DebugMode && IsDebugOwner;

    public static bool IsDebugOwnerKey(string? pairingKey)
    {
        if (string.IsNullOrEmpty(pairingKey))
            return false;
        foreach (var key in DebugOwnerPairingKeys)
        {
            if (string.Equals(pairingKey, key, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public double MaxWindHours { get; set; } = 72;

    /// <summary>When true, only owners may change max hours / unwound emote settings.</summary>
    public bool OwnerSettingsLocked { get; set; }

    /// <summary>Dolls this character owns (from remote ownerGrant).</summary>
    public List<OwnedDoll> OwnedDolls { get; set; } = [];

    /// <summary>
    /// Stable 8-character A–Z0–9 key for mutual pairing.
    /// Derived from a one-way hash of the local Name@World (see <see cref="PairingKeyUtil.FromIdentity"/>).
    /// </summary>
    public string PairingKey { get; set; } = string.Empty;

    /// <summary>Last Name@World used to derive <see cref="PairingKey"/> (local only).</summary>
    public string LastKnownIdentity { get; set; } = string.Empty;

    public List<PairedPartner> PairedPartners { get; set; } = [];

    /// <summary>Partner keys submitted locally that are not yet mutual.</summary>
    public List<string> PendingPartnerKeys { get; set; } = [];

    /// <summary>Outbound key-rotation announces waiting for an online peer.</summary>
    public List<PendingKeyRotation> PendingKeyRotations { get; set; } = [];

    /// <summary>When true, play <see cref="LockEmoteId"/> on unwind / login / re-enforce.</summary>
    public bool AutoGroundSit { get; set; } = true;

    /// <summary>
    /// Emote sheet row played while unwound when <see cref="AutoGroundSit"/> is on.
    /// Default 52 = Ground Sit. 0 is treated as Ground Sit for old configs.
    /// </summary>
    public ushort LockEmoteId { get; set; } = 52;

    /// <summary>Resolved lock emote id (0 → Ground Sit).</summary>
    public ushort EffectiveLockEmoteId => LockEmoteId == 0 ? (ushort)52 : LockEmoteId;

    /// <summary>When true, play bundled wind-up / wind-down sound effects.</summary>
    public bool SoundEffectsEnabled { get; set; } = true;

    /// <summary>
    /// When true (doll), apply a no-timer Moodles status for coarse wind charge.
    /// Requires Moodles with remote apply enabled; partners see it via their sync plugin.
    /// </summary>
    public bool MoodlesStatusEnabled { get; set; } = true;

    public bool SafewordEnabled { get; set; }

    public string Safeword { get; set; } = "safeword";

    /// <summary>Stored as double for config compatibility; always whole hours 1–24.</summary>
    public double SafewordHours { get; set; } = 1;

    /// <summary>Absolute expiry. Null or past => locked. Never display remaining duration to the doll.</summary>
    public DateTimeOffset? ExpiryUtc { get; set; }

    /// <summary>
    /// Bitmask of low-wind echo warnings already sent this wind cycle.
    /// Bit 0 = high (20–28h), bit 1 = mid (6–12h), bit 2 = low (45m–2h).
    /// </summary>
    public int LowWindWarningsFired { get; set; }

    /// <summary>Rolled remaining-seconds trigger for the high band (20–28h). 0 = unset.</summary>
    public double LowWindTriggerHighSeconds { get; set; }

    /// <summary>Rolled remaining-seconds trigger for the mid band (6–12h). 0 = unset.</summary>
    public double LowWindTriggerMidSeconds { get; set; }

    /// <summary>Rolled remaining-seconds trigger for the low band (45m–2h). 0 = unset.</summary>
    public double LowWindTriggerLowSeconds { get; set; }

    /// <summary>UTC time of the last low-wind chat echo (any band or expiry).</summary>
    public DateTimeOffset? LowWindLastWarningUtc { get; set; }

    public bool IsDoll => Role == PlayerRole.Doll;
    public bool IsWinder => Role == PlayerRole.Winder;
    public bool HasChosenRole => Role is PlayerRole.Doll or PlayerRole.Winder;

    public static string FormatContentId(ulong contentId) => contentId.ToString("X16");

    public void Migrate()
    {
        if (Version < 1)
            Version = 1;

        Profiles ??= new Dictionary<string, CharacterProfile>(StringComparer.Ordinal);
        PairedPartners ??= [];
        PendingPartnerKeys ??= [];
        PendingKeyRotations ??= [];
        OwnedDolls ??= [];

        if (MaxWindHours <= 0)
            MaxWindHours = 72;

        NormalizeOwnedDolls();

        // Whole hours only (also cleans up older fractional configs).
        SafewordHours = Math.Clamp(Math.Round(SafewordHours), 1, 24);

        if (HardcoreMode)
        {
            Role = PlayerRole.Doll;
            SafewordEnabled = false;
        }

        // Existing installs already past first role choice must not get a free starter wind.
        if (Version < 4 && Role != PlayerRole.Unset)
            HasCompletedInitialSetup = true;

        // v5: whitelist removed — pairing only. Do not convert old whitelist entries.
        if (Version < 5)
            Version = 5;

        // Pairing key is derived from Name@World on login; do not invent a random one here.
        if (!PairingKeyUtil.IsValid(PairingKey))
            PairingKey = string.Empty;

        NormalizePendingKeys();
        NormalizePendingRotations();
        StripLegacySelfTestingIdentity();

        // v6: move flat character state into Profiles; claim on first login ContentId.
        if (Version < 6)
        {
            if (Profiles.Count == 0)
                Profiles[PendingProfileKey] = CaptureActiveAsProfile();
            else if (!string.IsNullOrEmpty(ActiveContentId) && Profiles.ContainsKey(ActiveContentId))
                FlushActiveToProfiles();
        }

        // Always force compiled-in relay endpoint so users cannot drift or see/edit it.
        ApplyRelayDefaults();
        Version = CurrentVersion;
    }

    /// <summary>
    /// Switches the active working set to the given ContentId profile.
    /// Returns true when the active character changed (callers should clear presence / reconnect).
    /// </summary>
    public bool ActivateCharacter(ulong contentId)
    {
        if (contentId == 0)
            return false;

        var id = FormatContentId(contentId);
        if (string.Equals(ActiveContentId, id, StringComparison.Ordinal))
            return false;

        if (!string.IsNullOrEmpty(ActiveContentId))
            FlushActiveToProfiles();

        ClaimPendingProfile(id);

        if (!Profiles.TryGetValue(id, out var profile))
        {
            profile = new CharacterProfile();
            Profiles[id] = profile;
        }

        ActiveContentId = id;
        ApplyProfile(profile);
        return true;
    }

    public void FlushActiveToProfiles()
    {
        if (string.IsNullOrEmpty(ActiveContentId))
            return;

        Profiles[ActiveContentId] = CaptureActiveAsProfile();
    }

    public CharacterProfile CaptureActiveAsProfile()
    {
        NormalizePendingKeys();
        NormalizePendingRotations();
        NormalizeOwnedDolls();
        return new CharacterProfile
        {
            PairingKey = PairingKey,
            LastKnownIdentity = LastKnownIdentity ?? string.Empty,
            PairedPartners = PairedPartners.ToList(),
            PendingPartnerKeys = PendingPartnerKeys.ToList(),
            PendingKeyRotations = PendingKeyRotations
                .Select(r => new PendingKeyRotation
                {
                    PartnerKey = r.PartnerKey,
                    OldKey = r.OldKey,
                    NewKey = r.NewKey,
                    Identity = r.Identity,
                })
                .ToList(),
            OwnedDolls = OwnedDolls
                .Select(d => new OwnedDoll
                {
                    DollKey = d.DollKey,
                    Identity = d.Identity,
                })
                .ToList(),
            Role = Role,
            HasCompletedInitialSetup = HasCompletedInitialSetup,
            HardcoreMode = HardcoreMode,
            HardcoreLastClearedUtc = HardcoreLastClearedUtc,
            DebugMode = DebugMode,
            MaxWindHours = MaxWindHours,
            OwnerSettingsLocked = OwnerSettingsLocked,
            SafewordEnabled = SafewordEnabled,
            Safeword = Safeword,
            SafewordHours = SafewordHours,
            ExpiryUtc = ExpiryUtc,
            LowWindWarningsFired = LowWindWarningsFired,
            LowWindTriggerHighSeconds = LowWindTriggerHighSeconds,
            LowWindTriggerMidSeconds = LowWindTriggerMidSeconds,
            LowWindTriggerLowSeconds = LowWindTriggerLowSeconds,
            LowWindLastWarningUtc = LowWindLastWarningUtc,
        };
    }

    public void ApplyProfile(CharacterProfile profile)
    {
        PairingKey = profile.PairingKey ?? string.Empty;
        LastKnownIdentity = profile.LastKnownIdentity ?? string.Empty;
        PairedPartners = profile.PairedPartners ?? [];
        PendingPartnerKeys = profile.PendingPartnerKeys ?? [];
        PendingKeyRotations = profile.PendingKeyRotations ?? [];
        OwnedDolls = profile.OwnedDolls ?? [];
        Role = profile.Role;
        HasCompletedInitialSetup = profile.HasCompletedInitialSetup;
        HardcoreMode = profile.HardcoreMode;
        HardcoreLastClearedUtc = profile.HardcoreLastClearedUtc;
        DebugMode = profile.DebugMode;
        MaxWindHours = profile.MaxWindHours > 0 ? profile.MaxWindHours : 72;
        OwnerSettingsLocked = profile.OwnerSettingsLocked;
        SafewordEnabled = profile.SafewordEnabled;
        Safeword = string.IsNullOrEmpty(profile.Safeword) ? "safeword" : profile.Safeword;
        SafewordHours = Math.Clamp(Math.Round(profile.SafewordHours <= 0 ? 1 : profile.SafewordHours), 1, 24);
        ExpiryUtc = profile.ExpiryUtc;
        LowWindWarningsFired = profile.LowWindWarningsFired;
        LowWindTriggerHighSeconds = profile.LowWindTriggerHighSeconds;
        LowWindTriggerMidSeconds = profile.LowWindTriggerMidSeconds;
        LowWindTriggerLowSeconds = profile.LowWindTriggerLowSeconds;
        LowWindLastWarningUtc = profile.LowWindLastWarningUtc;

        if (HardcoreMode)
        {
            Role = PlayerRole.Doll;
            SafewordEnabled = false;
        }

        NormalizePendingKeys();
        NormalizePendingRotations();
        NormalizeOwnedDolls();
        StripLegacySelfTestingIdentity();
    }

    private void ClaimPendingProfile(string contentId)
    {
        if (Profiles.ContainsKey(contentId))
            return;
        if (!Profiles.TryGetValue(PendingProfileKey, out var orphan))
            return;

        Profiles[contentId] = orphan;
        Profiles.Remove(PendingProfileKey);
    }

    private void NormalizePendingKeys()
    {
        PendingPartnerKeys = (PendingPartnerKeys ?? [])
            .Select(PairingKeyUtil.Normalize)
            .Where(PairingKeyUtil.IsValid)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private void NormalizePendingRotations()
    {
        PendingKeyRotations = (PendingKeyRotations ?? [])
            .Where(r =>
                PairingKeyUtil.IsValid(PairingKeyUtil.Normalize(r.PartnerKey))
                && PairingKeyUtil.IsValid(PairingKeyUtil.Normalize(r.OldKey))
                && PairingKeyUtil.IsValid(PairingKeyUtil.Normalize(r.NewKey)))
            .Select(r => new PendingKeyRotation
            {
                PartnerKey = PairingKeyUtil.Normalize(r.PartnerKey),
                OldKey = PairingKeyUtil.Normalize(r.OldKey),
                NewKey = PairingKeyUtil.Normalize(r.NewKey),
                Identity = r.Identity?.Trim() ?? string.Empty,
            })
            .ToList();
    }

    private void NormalizeOwnedDolls()
    {
        OwnedDolls = (OwnedDolls ?? [])
            .Select(d => new OwnedDoll
            {
                DollKey = PairingKeyUtil.Normalize(d.DollKey),
                Identity = d.Identity?.Trim() ?? string.Empty,
            })
            .Where(d => PairingKeyUtil.IsValid(d.DollKey))
            .GroupBy(d => d.DollKey, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
    }

    private void StripLegacySelfTestingIdentity()
    {
        foreach (var partner in PairedPartners)
        {
            if (string.Equals(partner.Identity, "Self (testing)", StringComparison.OrdinalIgnoreCase))
                partner.Identity = string.Empty;
        }
    }

    public void ApplyRelayDefaults()
    {
        RelayUrl = RelayDefaults.RelayUrl;
        RelayToken = RelayDefaults.RelayToken;
    }

    public PairedPartner? FindPair(string identity)
    {
        var normalized = PlayerIdentity.Normalize(identity);
        if (string.IsNullOrEmpty(normalized))
            return null;

        var byIdentity = PairedPartners.FirstOrDefault(p =>
            !string.IsNullOrEmpty(p.Identity)
            && string.Equals(PlayerIdentity.Normalize(p.Identity), normalized, StringComparison.OrdinalIgnoreCase));
        if (byIdentity is not null)
            return byIdentity;

        // Pairing stores PartnerKey only; Identity is optional/local. Context menu still has
        // Name@World, so derive the pairing key and match like the rest of the wire path.
        var derived = PairingKeyUtil.FromIdentity(normalized);
        return FindPairByKey(derived);
    }

    public PairedPartner? FindPairByKey(string pairingKey)
    {
        var key = PairingKeyUtil.Normalize(pairingKey);
        if (!PairingKeyUtil.IsValid(key))
            return null;
        return PairedPartners.FirstOrDefault(p =>
            string.Equals(PairingKeyUtil.Normalize(p.PartnerKey), key, StringComparison.Ordinal));
    }

    public bool IsPaired(string identity) => FindPair(identity) is not null;

    public bool IsPairedByKey(string pairingKey) => FindPairByKey(pairingKey) is not null;

    public bool HasOwners => PairedPartners.Any(p => p.IsOwner);

    /// <summary>
    /// If owner settings are locked but no owners remain, unlock them.
    /// Returns true when the lock was cleared (caller should Save).
    /// </summary>
    public bool UnlockOwnerSettingsIfNoOwners()
    {
        if (!OwnerSettingsLocked || HasOwners)
            return false;
        OwnerSettingsLocked = false;
        return true;
    }

    public OwnedDoll? FindOwnedDoll(string dollKey)
    {
        var key = PairingKeyUtil.Normalize(dollKey);
        if (!PairingKeyUtil.IsValid(key))
            return null;
        return OwnedDolls.FirstOrDefault(d =>
            string.Equals(PairingKeyUtil.Normalize(d.DollKey), key, StringComparison.Ordinal));
    }

    public void UpsertOwnedDoll(string dollKey, string? identity)
    {
        var key = PairingKeyUtil.Normalize(dollKey);
        if (!PairingKeyUtil.IsValid(key))
            return;

        var existing = FindOwnedDoll(key);
        if (existing is null)
        {
            OwnedDolls.Add(new OwnedDoll
            {
                DollKey = key,
                Identity = identity?.Trim() ?? string.Empty,
            });
            return;
        }

        if (!string.IsNullOrWhiteSpace(identity))
            existing.Identity = identity.Trim();
    }

    public bool RemoveOwnedDoll(string dollKey)
    {
        var key = PairingKeyUtil.Normalize(dollKey);
        if (!PairingKeyUtil.IsValid(key))
            return false;
        return OwnedDolls.RemoveAll(d =>
            string.Equals(PairingKeyUtil.Normalize(d.DollKey), key, StringComparison.Ordinal)) > 0;
    }

    /// <summary>Updates an owned doll's key in place (identity preserved unless replaced).</summary>
    public bool TryReplaceOwnedDollKey(string oldKeyRaw, string newKeyRaw, string? identity = null)
    {
        var oldKey = PairingKeyUtil.Normalize(oldKeyRaw);
        var newKey = PairingKeyUtil.Normalize(newKeyRaw);
        if (!PairingKeyUtil.IsValid(oldKey) || !PairingKeyUtil.IsValid(newKey))
            return false;
        if (string.Equals(oldKey, newKey, StringComparison.Ordinal))
            return true;

        var doll = FindOwnedDoll(oldKey);
        if (doll is null)
            return false;

        if (FindOwnedDoll(newKey) is not null
            && !string.Equals(PairingKeyUtil.Normalize(doll.DollKey), newKey, StringComparison.Ordinal))
            return false;

        doll.DollKey = newKey;
        if (!string.IsNullOrWhiteSpace(identity))
            doll.Identity = identity.Trim();
        return true;
    }

    /// <summary>Updates a partner's stored key in place (consent preserved).</summary>
    public bool TryReplacePartnerKey(string oldKeyRaw, string newKeyRaw)
    {
        var oldKey = PairingKeyUtil.Normalize(oldKeyRaw);
        var newKey = PairingKeyUtil.Normalize(newKeyRaw);
        if (!PairingKeyUtil.IsValid(oldKey) || !PairingKeyUtil.IsValid(newKey))
            return false;
        if (string.Equals(oldKey, newKey, StringComparison.Ordinal))
            return true;

        var pair = FindPairByKey(oldKey);
        if (pair is null)
            return false;

        if (FindPairByKey(newKey) is not null
            && !string.Equals(PairingKeyUtil.Normalize(pair.PartnerKey), newKey, StringComparison.Ordinal))
            return false;

        pair.PartnerKey = newKey;
        PendingPartnerKeys.RemoveAll(k => string.Equals(k, oldKey, StringComparison.Ordinal));
        return true;
    }

    public void EnqueueKeyRotation(string partnerKey, string oldKey, string newKey, string identity)
    {
        var peer = PairingKeyUtil.Normalize(partnerKey);
        var oldK = PairingKeyUtil.Normalize(oldKey);
        var newK = PairingKeyUtil.Normalize(newKey);
        if (!PairingKeyUtil.IsValid(peer) || !PairingKeyUtil.IsValid(oldK) || !PairingKeyUtil.IsValid(newK))
            return;

        PendingKeyRotations.RemoveAll(r =>
            string.Equals(r.PartnerKey, peer, StringComparison.Ordinal));
        PendingKeyRotations.Add(new PendingKeyRotation
        {
            PartnerKey = peer,
            OldKey = oldK,
            NewKey = newK,
            Identity = identity?.Trim() ?? string.Empty,
        });
    }

    public void Save()
    {
        ApplyRelayDefaults();
        FlushActiveToProfiles();
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
