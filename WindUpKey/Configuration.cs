using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Configuration;
using WindUpKey.Protocol;

namespace WindUpKey;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 5;

    public int Version { get; set; } = CurrentVersion;

    public string RelayUrl { get; set; } = RelayDefaults.RelayUrl;

    /// <summary>Shared relay token. Never log this value.</summary>
    public string RelayToken { get; set; } = RelayDefaults.RelayToken;

    /// <summary>Unset until first-launch role prompt.</summary>
    public PlayerRole Role { get; set; } = PlayerRole.Unset;

    /// <summary>True after the first Doll/Winder choice. Starter wind is only granted before this.</summary>
    public bool HasCompletedInitialSetup { get; set; }

    /// <summary>When true, role is locked to Doll and cannot switch to Winder.</summary>
    public bool HardcoreMode { get; set; }

    public double MaxWindHours { get; set; } = 72;

    /// <summary>Stable 8-character A–Z0–9 key for mutual pairing.</summary>
    public string PairingKey { get; set; } = string.Empty;

    public List<PairedPartner> PairedPartners { get; set; } = [];

    /// <summary>Partner keys submitted locally that are not yet mutual.</summary>
    public List<string> PendingPartnerKeys { get; set; } = [];

    /// <summary>When true, run /groundsit on unwind / login / re-enforce.</summary>
    public bool AutoGroundSit { get; set; } = true;

    public bool SafewordEnabled { get; set; }

    public string Safeword { get; set; } = "safeword";

    /// <summary>Stored as double for config compatibility; always whole hours 1–24.</summary>
    public double SafewordHours { get; set; } = 1;

    /// <summary>Absolute expiry. Null or past => locked. Never display remaining duration to the doll.</summary>
    public DateTimeOffset? ExpiryUtc { get; set; }

    public bool IsDoll => Role == PlayerRole.Doll;
    public bool IsWinder => Role == PlayerRole.Winder;
    public bool HasChosenRole => Role is PlayerRole.Doll or PlayerRole.Winder;

    public void Migrate()
    {
        if (Version < 1)
            Version = 1;

        if (MaxWindHours <= 0)
            MaxWindHours = 72;

        // Whole hours only (also cleans up older fractional configs).
        SafewordHours = Math.Clamp(Math.Round(SafewordHours), 1, 24);

        PairedPartners ??= [];
        PendingPartnerKeys ??= [];

        if (HardcoreMode)
            Role = PlayerRole.Doll;

        // Existing installs already past first role choice must not get a free starter wind.
        if (Version < 4 && Role != PlayerRole.Unset)
            HasCompletedInitialSetup = true;

        // v5: whitelist removed — pairing only. Do not convert old whitelist entries.
        if (Version < 5)
        {
            // Drop legacy whitelist fields if deserialized into ignored extras; lists start fresh.
        }

        if (!PairingKeyUtil.IsValid(PairingKey))
            PairingKey = PairingKeyUtil.Generate();

        PendingPartnerKeys = PendingPartnerKeys
            .Select(PairingKeyUtil.Normalize)
            .Where(PairingKeyUtil.IsValid)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Older testing builds stored a fake "Self (testing)" identity on the local self-pair.
        foreach (var partner in PairedPartners)
        {
            if (string.Equals(partner.Identity, "Self (testing)", StringComparison.OrdinalIgnoreCase))
                partner.Identity = string.Empty;
        }

        // Always force compiled-in relay endpoint so users cannot drift or see/edit it.
        ApplyRelayDefaults();
        Version = CurrentVersion;
    }

    public void ApplyRelayDefaults()
    {
        RelayUrl = RelayDefaults.RelayUrl;
        RelayToken = RelayDefaults.RelayToken;
    }

    public PairedPartner? FindPair(string identity)
    {
        var normalized = PlayerIdentity.Normalize(identity);
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

    public void Save()
    {
        ApplyRelayDefaults();
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}