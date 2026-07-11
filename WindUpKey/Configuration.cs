using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace WindUpKey;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 3;

    public int Version { get; set; } = CurrentVersion;

    public string RelayUrl { get; set; } = RelayDefaults.RelayUrl;

    /// <summary>Shared relay token. Never log this value.</summary>
    public string RelayToken { get; set; } = RelayDefaults.RelayToken;

    /// <summary>Unset until first-launch role prompt.</summary>
    public PlayerRole Role { get; set; } = PlayerRole.Unset;

    /// <summary>When true, role is locked to Doll and cannot switch to Winder.</summary>
    public bool HardcoreMode { get; set; }

    public double MaxWindHours { get; set; } = 72;

    public bool WhitelistEnabled { get; set; }

    public List<string> Whitelist { get; set; } = [];

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

        Whitelist ??= [];

        if (HardcoreMode)
            Role = PlayerRole.Doll;

        // Always force compiled-in relay endpoint so users cannot drift or see/edit it.
        ApplyRelayDefaults();
        Version = CurrentVersion;
    }

    public void ApplyRelayDefaults()
    {
        RelayUrl = RelayDefaults.RelayUrl;
        RelayToken = RelayDefaults.RelayToken;
    }

    public void Save()
    {
        ApplyRelayDefaults();
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
