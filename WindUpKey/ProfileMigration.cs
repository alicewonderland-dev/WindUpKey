using System;
using System.Collections.Generic;

namespace WindUpKey;

/// <summary>
/// Config migrations of <see cref="CharacterProfile"/> values that don't need Dalamud, so they can be tested.
/// <see cref="Configuration.Migrate"/> calls them.
/// </summary>
public static class ProfileMigration
{
    /// <summary>The "play emote when unwound" of a profile created after 06-A1.</summary>
    public const bool NewProfileAutoGroundSit = true;

    /// <summary>The unwound emote of a profile created after 06-A1 (Sit on Ground).</summary>
    public const ushort NewProfileLockEmoteId = CharacterProfile.GroundSitEmoteId;

    /// <summary>
    /// 06-A1: the lock emote settings became per character. Every profile (the <c>pending</c> orphan too) whose
    /// <see cref="CharacterProfile.AutoGroundSit"/> or <see cref="CharacterProfile.LockEmoteId"/> is still null gets the
    /// global value it used until now. A value already set is never overwritten. This is gated on the nulls, not on
    /// <see cref="Configuration.Version"/>: an older build drops the fields when it saves, so going back to it and
    /// forward again refills only what it dropped, from the globals it kept as the active character's values.
    /// 06-A13: returns nothing: no caller used whether anything was filled.
    /// </summary>
    /// <summary>
    /// 14 (spare springs): a profile without wind layers yet (<see cref="CharacterProfile.WindAsOfUtc"/> null) gets them
    /// from its expiry: the time left up to max wind hours is normal wind, anything past it quest overwind. Gated on the
    /// null, not the version, so an older build that dropped the layers on save has them refilled from the expiry it kept.
    /// </summary>
    public static void FillWindLayers(IEnumerable<CharacterProfile?> profiles, DateTimeOffset now)
    {
        foreach (var profile in profiles)
        {
            if (profile is null || profile.WindAsOfUtc is not null)
                continue;

            var layers = Services.WindLayers.FromExpiry(profile.ExpiryUtc, now, profile.MaxWindHours > 0 ? profile.MaxWindHours : 72);
            profile.NormalWindHours = layers.Normal;
            profile.QuestOverwindHours = layers.Quest;
            profile.PlayerOverwindHours = 0;
            profile.BrokenUntilUtc = null;
            profile.WindAsOfUtc = now;
        }
    }

    public static void FillLockEmote(IEnumerable<CharacterProfile?> profiles, bool globalAutoGroundSit, ushort globalLockEmoteId)
    {
        foreach (var profile in profiles)
        {
            if (profile is null)
                continue;

            if (profile.AutoGroundSit is null)
                profile.AutoGroundSit = globalAutoGroundSit;

            if (profile.LockEmoteId is null)
                profile.LockEmoteId = globalLockEmoteId;
        }
    }
}
