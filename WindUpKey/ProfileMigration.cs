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
