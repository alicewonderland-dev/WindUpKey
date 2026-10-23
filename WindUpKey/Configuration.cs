using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Configuration;
using WindUpKey.Protocol;
using WindUpKey.Services;
using WindUpKey.Quest;

namespace WindUpKey;

[Serializable]
public class Configuration : IPluginConfiguration
{
    /// <summary>
    /// 12: per-character lock emote (06-A1). 06-B1's <see cref="SafewordPausedUntilUtc"/> needs no bump: absent means no
    /// pause. 13: the quest board (<see cref="QuestBoard"/>, <see cref="Achievements"/>) replaced the daily quest fields,
    /// which are dropped without reward. 14: spare springs: the wind layers (<see cref="NormalWindHours"/> …), filled
    /// from the expiry by <see cref="ProfileMigration.FillWindLayers"/>, and <see cref="BrokenRecoveryMinutes"/>.
    /// </summary>
    public const int CurrentVersion = 14;

    /// <summary>Orphan profile from pre-v6 flat config until the first logged-in ContentId claims it.</summary>
    public const string PendingProfileKey = "pending";

    public int Version { get; set; } = CurrentVersion;

    public string RelayUrl { get; set; } = RelayDefaults.RelayUrl;

    /// <summary>
    /// Last Funnel URL that accepted a WebSocket (Linux or Windows host).
    /// Used to prefer that host on the next connect; never shown in UI.
    /// </summary>
    public string LastSuccessfulRelayUrl { get; set; } = string.Empty;

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
    /// ContentId hex values that may enable debug/testing features
    /// (Alice Selena@Sargatanas). Add further tester ContentIds as needed.
    /// </summary>
    public static readonly string[] DebugOwnerContentIds = ["004000174AA8BCC2"];

    /// <summary>When true, unlocks debug/testing features (self-wind, unwind UI, /windup check, /windup debug).</summary>
    public bool DebugMode { get; set; }

    /// <summary>True when the active character ContentId is a debug owner.</summary>
    public bool IsDebugOwner => IsDebugOwnerContentId(ActiveContentId);

    /// <summary>Debug features require both the toggle and an owner ContentId.</summary>
    public bool IsDebugEnabled => DebugMode && IsDebugOwner;

    public static bool IsDebugOwnerContentId(string? contentIdHex)
    {
        if (string.IsNullOrEmpty(contentIdHex))
            return false;
        foreach (var id in DebugOwnerContentIds)
        {
            if (string.Equals(contentIdHex, id, StringComparison.OrdinalIgnoreCase))
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
    /// Derived from a one-way hash of the local ContentId (see <see cref="PairingKeyUtil.FromContentId"/>).
    /// </summary>
    public string PairingKey { get; set; } = string.Empty;

    /// <summary>Last Name@World for local labels only (not used for pairing-key derivation).</summary>
    public string LastKnownIdentity { get; set; } = string.Empty;

    public List<PairedPartner> PairedPartners { get; set; } = [];

    /// <summary>Partner keys submitted locally that are not yet mutual.</summary>
    public List<string> PendingPartnerKeys { get; set; } = [];

    /// <summary>Unpairs not yet sent to the relay (made offline, or the send failed); sent on the next connect.</summary>
    public List<PendingPairRemoval> PendingPairRemovals { get; set; } = [];

    /// <summary>
    /// When true, play <see cref="LockEmoteId"/> on unwind / login / re-enforce.
    /// 06-A1: per character since config version 12 (<see cref="CharacterProfile.AutoGroundSit"/>); this flat value is
    /// the active character's mirror, like the other per-character fields, so an older build that only knows the flat
    /// value still reads the last active character's choice. Downgrading loses the other characters' own choices:
    /// the older build saves profiles without them, and coming back fills them from this value.
    /// </summary>
    public bool AutoGroundSit { get; set; } = true;

    /// <summary>
    /// Emote sheet row played while unwound when <see cref="AutoGroundSit"/> is on.
    /// Default <see cref="CharacterProfile.GroundSitEmoteId"/> (52, Ground Sit; 08-T7). 0 is treated as Ground Sit for
    /// old configs.
    /// 06-A1: per character, mirrored like <see cref="AutoGroundSit"/>.
    /// </summary>
    public ushort LockEmoteId { get; set; } = CharacterProfile.GroundSitEmoteId;

    /// <summary>Resolved lock emote id (0 → Ground Sit).</summary>
    public ushort EffectiveLockEmoteId => LockEmoteId == 0 ? CharacterProfile.GroundSitEmoteId : LockEmoteId;

    /// <summary>When true, play bundled wind-up / wind-down sound effects.</summary>
    public bool SoundEffectsEnabled { get; set; } = true;

    /// <summary>
    /// When true (doll), apply a no-timer Moodles status for coarse wind charge.
    /// Requires Moodles with remote apply enabled; partners see it via their sync plugin.
    /// Off by default, since others see it: a doll opts in. Existing configs keep their saved value.
    /// </summary>
    public bool MoodlesStatusEnabled { get; set; }

    /// <summary>
    /// When true, re-run Moodles' own automation after each area change. Moodles only runs it
    /// on login and job change, so automation moodles removed by hand otherwise stay off.
    /// </summary>
    public bool ReapplyMoodlesAutomationOnAreaChange { get; set; }

    public bool SafewordEnabled { get; set; }

    public string Safeword { get; set; } = "safeword";

    /// <summary>Stored as double for config compatibility; always whole hours 1–24.</summary>
    public double SafewordHours { get; set; } = 1;

    /// <summary>
    /// 06-B1: until when the safeword pauses remote control, or null (per character, mirrored like the fields above).
    /// Read it through <see cref="SafewordPause"/>, which ignores an end that has passed, clamps one more than 24 h
    /// ahead, and never counts it in Hardcore.
    /// </summary>
    public DateTimeOffset? SafewordPausedUntilUtc { get; set; }

    /// <summary>Absolute expiry. Null or past => locked. Never display remaining duration to the doll.</summary>
    public DateTimeOffset? ExpiryUtc { get; set; }

    /// <summary>Last-write-wins stamp for <see cref="ExpiryUtc"/> sync with the relay.</summary>
    public DateTimeOffset? WindUpdatedUtc { get; set; }

    /// <summary>
    /// 14: the active character's wind layers (<see cref="WindLayers"/>), mirrored like the fields above. Read and write
    /// them through <see cref="WindTimerService"/>; <see cref="ExpiryUtc"/> is derived from them.
    /// </summary>
    public double NormalWindHours { get; set; }

    public double QuestOverwindHours { get; set; }

    public double PlayerOverwindHours { get; set; }

    public DateTimeOffset? WindAsOfUtc { get; set; }

    public DateTimeOffset? BrokenUntilUtc { get; set; }

    public int BrokenRecoveryMinutes { get; set; } = WindLayers.DefaultRecoveryMinutes;

    /// <summary>Last paired-partner wind request sent by this doll; shared across all partners.</summary>
    public DateTimeOffset? LastWindRequestUtc { get; set; }

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

    /// <summary>The active character's quest board (per character, mirrored like the fields above).</summary>
    public QuestBoardState QuestBoard { get; set; } = new();

    /// <summary>The active character's achievement flags (<see cref="AchievementFlags"/>), mirrored like the fields above.</summary>
    public Dictionary<string, DateTimeOffset> Achievements { get; set; } = new(StringComparer.Ordinal);

    public bool IsDoll => Role == PlayerRole.Doll;
    public bool IsWinder => Role == PlayerRole.Winder;
    public bool HasChosenRole => Role is PlayerRole.Doll or PlayerRole.Winder;

    public static string FormatContentId(ulong contentId) => contentId.ToString("X16");

    public void Migrate()
    {
        Profiles ??= new Dictionary<string, CharacterProfile>(StringComparer.Ordinal);
        // 06-A1: before any profile is activated, every profile (pending too) without its own lock emote settings
        // gets the global values. Gated on the nulls, never on Version, so a set value is never overwritten.
        ProfileMigration.FillLockEmote(Profiles.Values, AutoGroundSit, LockEmoteId);
        // 14: wind layers from each profile's expiry (and the active character's flat fields, below).
        var migratedAt = DateTimeOffset.UtcNow;
        ProfileMigration.FillWindLayers(Profiles.Values, migratedAt);
        if (WindAsOfUtc is null)
        {
            var layers = WindLayers.FromExpiry(ExpiryUtc, migratedAt, MaxWindHours > 0 ? MaxWindHours : 72);
            NormalWindHours = layers.Normal;
            QuestOverwindHours = layers.Quest;
            PlayerOverwindHours = 0;
            BrokenUntilUtc = null;
            WindAsOfUtc = migratedAt;
        }

        BrokenRecoveryMinutes = WindLayers.ClampRecoveryMinutes(BrokenRecoveryMinutes <= 0 ? WindLayers.DefaultRecoveryMinutes : BrokenRecoveryMinutes);
        PairedPartners ??= [];
        PendingPartnerKeys ??= [];
        PendingPairRemovals ??= [];
        OwnedDolls ??= [];

        NormalizePartnerLabels(PairedPartners);
        foreach (var profile in Profiles.Values)
        {
            if (profile?.PairedPartners is not null)
                NormalizePartnerLabels(profile.PairedPartners);
            if (profile is null)
                continue;
            profile.QuestBoard ??= new QuestBoardState();
            profile.QuestBoard.Normalize();
            profile.Achievements = NormalizeAchievements(profile.Achievements);
        }

        if (MaxWindHours <= 0)
            MaxWindHours = 72;

        NormalizeOwnedDolls();

        // Whole hours only (also cleans up older fractional configs).
        SafewordHours = Math.Clamp(Math.Round(SafewordHours), 1, 24);

        // 06-B1: the active character's pause (a reload as the same character applies no profile): a passed end is
        // dropped, and one more than 24 h ahead is clamped.
        SafewordPausedUntilUtc = SafewordPause.Normalize(SafewordPausedUntilUtc, DateTimeOffset.UtcNow);

        if (HardcoreMode)
        {
            Role = PlayerRole.Doll;
            SafewordEnabled = false;
            SafewordPausedUntilUtc = null;
        }

        // Pairing key is seeded from ContentId when empty; do not invent a random one here.
        if (!PairingKeyUtil.IsValid(PairingKey))
            PairingKey = string.Empty;

        NormalizePendingKeys();

        // 13: the quest board replaced the daily quests; their old fields are simply not read any more.
        QuestBoard ??= new QuestBoardState();
        QuestBoard.Normalize();
        Achievements = NormalizeAchievements(Achievements);

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
            // 06-A1: a new character starts with its own default lock emote, not the previous character's.
            profile = new CharacterProfile
            {
                AutoGroundSit = ProfileMigration.NewProfileAutoGroundSit,
                LockEmoteId = ProfileMigration.NewProfileLockEmoteId,
            };
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
        NormalizeOwnedDolls();
        NormalizePartnerLabels(PairedPartners);
        return new CharacterProfile
        {
            PairingKey = PairingKey,
            LastKnownIdentity = LastKnownIdentity ?? string.Empty,
            PairedPartners = PairedPartners.ToList(),
            PendingPartnerKeys = PendingPartnerKeys.ToList(),
            PendingPairRemovals = PairRemovalQueue.Normalize(PendingPairRemovals),
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
            AutoGroundSit = AutoGroundSit,
            LockEmoteId = LockEmoteId,
            SafewordEnabled = SafewordEnabled,
            Safeword = Safeword,
            SafewordHours = SafewordHours,
            SafewordPausedUntilUtc = SafewordPausedUntilUtc,
            ExpiryUtc = ExpiryUtc,
            WindUpdatedUtc = WindUpdatedUtc,
            NormalWindHours = NormalWindHours,
            QuestOverwindHours = QuestOverwindHours,
            PlayerOverwindHours = PlayerOverwindHours,
            WindAsOfUtc = WindAsOfUtc,
            BrokenUntilUtc = BrokenUntilUtc,
            BrokenRecoveryMinutes = BrokenRecoveryMinutes,
            LastWindRequestUtc = LastWindRequestUtc,
            LowWindWarningsFired = LowWindWarningsFired,
            LowWindTriggerHighSeconds = LowWindTriggerHighSeconds,
            LowWindTriggerMidSeconds = LowWindTriggerMidSeconds,
            LowWindTriggerLowSeconds = LowWindTriggerLowSeconds,
            LowWindLastWarningUtc = LowWindLastWarningUtc,
            QuestBoard = (QuestBoard ?? new QuestBoardState()).Clone(),
            Achievements = NormalizeAchievements(Achievements),
        };
    }

    public void ApplyProfile(CharacterProfile profile)
    {
        PairingKey = profile.PairingKey ?? string.Empty;
        LastKnownIdentity = profile.LastKnownIdentity ?? string.Empty;
        PairedPartners = profile.PairedPartners ?? [];
        PendingPartnerKeys = profile.PendingPartnerKeys ?? [];
        PendingPairRemovals = profile.PendingPairRemovals ?? [];
        OwnedDolls = profile.OwnedDolls ?? [];
        Role = profile.Role;
        HasCompletedInitialSetup = profile.HasCompletedInitialSetup;
        HardcoreMode = profile.HardcoreMode;
        HardcoreLastClearedUtc = profile.HardcoreLastClearedUtc;
        DebugMode = profile.DebugMode;
        MaxWindHours = profile.MaxWindHours > 0 ? profile.MaxWindHours : 72;
        OwnerSettingsLocked = profile.OwnerSettingsLocked;
        // 06-A1: Migrate fills both before any activation; a null here (a migrate that failed) is a new profile's default.
        AutoGroundSit = profile.AutoGroundSit ?? ProfileMigration.NewProfileAutoGroundSit;
        LockEmoteId = profile.LockEmoteId ?? ProfileMigration.NewProfileLockEmoteId;
        SafewordEnabled = profile.SafewordEnabled;
        Safeword = string.IsNullOrEmpty(profile.Safeword) ? "safeword" : profile.Safeword;
        SafewordHours = Math.Clamp(Math.Round(profile.SafewordHours <= 0 ? 1 : profile.SafewordHours), 1, 24);
        // 06-B1: a passed pause is dropped, and one more than 24 h ahead (a clock that jumped back) is clamped.
        SafewordPausedUntilUtc = SafewordPause.Normalize(profile.SafewordPausedUntilUtc, DateTimeOffset.UtcNow);
        ExpiryUtc = profile.ExpiryUtc;
        WindUpdatedUtc = profile.WindUpdatedUtc;
        NormalWindHours = profile.NormalWindHours;
        QuestOverwindHours = profile.QuestOverwindHours;
        PlayerOverwindHours = profile.PlayerOverwindHours;
        WindAsOfUtc = profile.WindAsOfUtc;
        BrokenUntilUtc = profile.BrokenUntilUtc;
        BrokenRecoveryMinutes = WindLayers.ClampRecoveryMinutes(profile.BrokenRecoveryMinutes <= 0 ? WindLayers.DefaultRecoveryMinutes : profile.BrokenRecoveryMinutes);
        LastWindRequestUtc = profile.LastWindRequestUtc;
        LowWindWarningsFired = profile.LowWindWarningsFired;
        LowWindTriggerHighSeconds = profile.LowWindTriggerHighSeconds;
        LowWindTriggerMidSeconds = profile.LowWindTriggerMidSeconds;
        LowWindTriggerLowSeconds = profile.LowWindTriggerLowSeconds;
        LowWindLastWarningUtc = profile.LowWindLastWarningUtc;
        QuestBoard = profile.QuestBoard?.Clone() ?? new QuestBoardState();
        QuestBoard.Normalize();
        Achievements = NormalizeAchievements(profile.Achievements);

        if (HardcoreMode)
        {
            Role = PlayerRole.Doll;
            SafewordEnabled = false;
            // 06-B1: Hardcore has no safeword, so no pause either.
            SafewordPausedUntilUtc = null;
        }

        NormalizePendingKeys();
        NormalizeOwnedDolls();
        NormalizePartnerLabels(PairedPartners);
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
        PendingPairRemovals = PairRemovalQueue.Normalize(PendingPairRemovals);
    }

    private void NormalizeOwnedDolls()
    {
        OwnedDolls = (OwnedDolls ?? [])
            .Select(d => new OwnedDoll
            {
                DollKey = PairingKeyUtil.Normalize(d.DollKey),
                // 03-D11: the label came from the doll; one stored oversized by an older build is sanitised on the next save.
                Identity = RemoteText.Sanitize(d.Identity, WireLimits.ShortField),
            })
            .Where(d => PairingKeyUtil.IsValid(d.DollKey))
            .GroupBy(d => d.DollKey, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>A copy with the ordinal comparer, without blank ids (a missing or damaged config gives an empty one).</summary>
    private static Dictionary<string, DateTimeOffset> NormalizeAchievements(Dictionary<string, DateTimeOffset>? flags)
    {
        var normalized = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var (id, earned) in flags ?? [])
        {
            if (!string.IsNullOrWhiteSpace(id))
                normalized.TryAdd(id, earned);
        }

        return normalized;
    }

    private static void NormalizePartnerLabels(List<PairedPartner> partners)
    {
        foreach (var partner in partners)
        {
            partner.Identity = partner.Identity?.Trim() ?? string.Empty;
            partner.Nickname = partner.Nickname?.Trim() ?? string.Empty;
            partner.Title = partner.Title?.Trim() ?? string.Empty;
        }
    }

    public void ApplyRelayDefaults()
    {
        // Keep RelayUrl on the sticky host when it is still a compiled candidate;
        // otherwise fall back to the primary Funnel URL.
        var preferred = LastSuccessfulRelayUrl;
        RelayUrl = !string.IsNullOrWhiteSpace(preferred)
                   && RelayDefaults.RelayUrls.Any(u =>
                       string.Equals(u, preferred, StringComparison.OrdinalIgnoreCase))
            ? preferred
            : RelayDefaults.RelayUrl;
        RelayToken = RelayDefaults.RelayToken;
    }

    public PairedPartner? FindPair(string identity)
    {
        var normalized = PlayerIdentity.Normalize(identity);
        if (string.IsNullOrEmpty(normalized))
            return null;

        // Pairing keys are ContentId-derived; Name@World is only a local label on the pair.
        return PairedPartners.FirstOrDefault(p =>
            !string.IsNullOrEmpty(p.Identity)
            && string.Equals(PlayerIdentity.Normalize(p.Identity), normalized, StringComparison.OrdinalIgnoreCase));
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

        // 03-D11: a remote label, sanitised before it is stored.
        identity = RemoteText.Sanitize(identity, WireLimits.ShortField);
        var existing = FindOwnedDoll(key);
        if (existing is null)
        {
            OwnedDolls.Add(new OwnedDoll
            {
                DollKey = key,
                Identity = identity,
            });
            return;
        }

        if (!string.IsNullOrWhiteSpace(identity))
            existing.Identity = identity;
    }

    public bool RemoveOwnedDoll(string dollKey)
    {
        var key = PairingKeyUtil.Normalize(dollKey);
        if (!PairingKeyUtil.IsValid(key))
            return false;
        return OwnedDolls.RemoveAll(d =>
            string.Equals(PairingKeyUtil.Normalize(d.DollKey), key, StringComparison.Ordinal)) > 0;
    }

    public void Save()
    {
        ApplyRelayDefaults();
        FlushActiveToProfiles();
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
