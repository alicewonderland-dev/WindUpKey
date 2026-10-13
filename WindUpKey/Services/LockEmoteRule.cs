namespace WindUpKey.Services;

/// <summary>
/// 06-C6 (owner decision): which emotes can be the unwound (lock) emote. <c>/doze</c> can't, nor can any other emote
/// whose EmoteMode is a position loop (ConditionMode 11, the character's InPositionLoop mode), except the two sits,
/// which stand up with <c>/sit</c>: a wound doll is never left in a doze-like pose only the interrupt command ends. In the
/// Emote sheet <c>/doze</c> is row 13 with no EmoteMode of its own (the doze loop is row 88, Sleep, EmoteMode 3), so it
/// is named here; Sit (row 50, EmoteMode 2) and Ground Sit (row 52, EmoteMode 1) share ConditionMode 11 and stay allowed.
/// An excluded emote is used as Ground Sit: a stored one is left in the config (a downgrade keeps it), and one that
/// arrives in an owner's settings update is stored as Ground Sit. No Dalamud dependency: <c>GameCommandRunner</c> reads the sheet.
/// </summary>
public static class LockEmoteRule
{
    /// <summary>Emote sheet row for Doze (<c>/doze</c>).</summary>
    public const ushort DozeEmoteId = 13;

    /// <summary>EmoteMode.ConditionMode of a position loop (sit, groundsit, doze): the character's InPositionLoop mode.</summary>
    public const byte InPositionLoopConditionMode = 11;

    /// <summary>
    /// Whether <paramref name="emoteId"/> can be the lock emote, given its EmoteMode's ConditionMode (0 without an
    /// EmoteMode) and whether it is Sit or Ground Sit (<paramref name="isSitOrGroundSit"/>).
    /// </summary>
    public static bool IsAllowed(ushort emoteId, byte conditionMode, bool isSitOrGroundSit) =>
        emoteId != DozeEmoteId && (isSitOrGroundSit || conditionMode != InPositionLoopConditionMode);

    /// <summary>
    /// The lock emote to store from an owner's settings update (<c>ownerSettingsUpdate</c>, which an old owner client
    /// may send with an excluded emote): 0 or an excluded emote is applied as Ground Sit, not refused.
    /// </summary>
    public static ushort FromRemote(ushort emoteId, bool allowed) =>
        emoteId == 0 || !allowed ? CharacterProfile.GroundSitEmoteId : emoteId;
}
