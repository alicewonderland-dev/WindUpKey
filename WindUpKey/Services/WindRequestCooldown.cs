using System;

namespace WindUpKey.Services;

/// <summary>
/// 09-K4: a wind request's one-hour cooldown starts before the send, so a double click can't send twice. When the send
/// doesn't go out, the cooldown is given back only while it still holds that request's own stamp on the character that
/// sent it: another character's cooldown (after a switch) and a newer stamp are left alone. No Dalamud dependency.
/// </summary>
public static class WindRequestCooldown
{
    /// <summary>
    /// True when the cooldown now set (<paramref name="current"/>, on <paramref name="currentContentId"/>) is still the
    /// <paramref name="stamp"/> a failed request set on <paramref name="stampContentId"/>, so it may be put back.
    /// </summary>
    public static bool ShouldRollBack(
        DateTimeOffset? current,
        string currentContentId,
        DateTimeOffset stamp,
        string stampContentId) =>
        current == stamp && string.Equals(currentContentId, stampContentId, StringComparison.Ordinal);
}
