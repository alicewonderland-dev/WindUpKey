using System;
using WindUpKey.Protocol;

namespace WindUpKey.Services;

/// <summary>
/// 06-B1: the pause of remote control that using the safeword starts. While it lasts, the doll refuses an owner's
/// unwind, call and settings update, replying <see cref="ErrorCodes.SafewordPaused"/>; winding, settings queries,
/// presence and pairing still work. It lasts the safeword hours, can be ended early by the doll, and never applies in
/// Hardcore. No Dalamud dependency.
/// </summary>
public static class SafewordPause
{
    /// <summary>
    /// The longest a pause lasts (the safeword hours are 1–24), and the furthest ahead a stored end is believed: one
    /// further ahead (a clock that jumped back, or an edited config) counts as ending this long from now.
    /// </summary>
    public static readonly TimeSpan MaxLength = TimeSpan.FromHours(24);

    /// <summary><c>/windup safeword resume</c> ends a pause early (see <see cref="IsResume"/>).</summary>
    public const string ResumeWord = "resume";

    /// <summary>
    /// A stored pause end as it counts at <paramref name="now"/>: null when there is none or it has passed, and at most
    /// <see cref="MaxLength"/> from now.
    /// </summary>
    public static DateTimeOffset? Normalize(DateTimeOffset? stored, DateTimeOffset now)
    {
        if (stored is not { } until || until <= now)
            return null;

        var latest = now + MaxLength;
        return until > latest ? latest : until;
    }

    /// <summary>When the active pause ends, or null when remote control isn't paused. Hardcore never has a pause.</summary>
    public static DateTimeOffset? EndsAt(DateTimeOffset? stored, DateTimeOffset now, bool hardcore) =>
        hardcore ? null : Normalize(stored, now);

    /// <summary>Whether remote control is paused at <paramref name="now"/>.</summary>
    public static bool IsActive(DateTimeOffset? stored, DateTimeOffset now, bool hardcore) =>
        EndsAt(stored, now, hardcore) is not null;

    /// <summary>
    /// The pause end after the safeword is used: <c>max(current, now + hours)</c>, so using it again never shortens a
    /// pause. <paramref name="hours"/> is the safeword hours, kept within 1–24 as the setting is.
    /// </summary>
    public static DateTimeOffset Extend(DateTimeOffset? stored, DateTimeOffset now, double hours)
    {
        var proposed = now + TimeSpan.FromHours(Math.Clamp(hours, 1, MaxLength.TotalHours));
        return Normalize(stored, now) is { } current && current > proposed ? current : proposed;
    }

    /// <summary>
    /// The inbound messages a pause refuses: an owner's unwind, call and settings update, and (spare springs) a player's
    /// wind, which can now overwind and break the doll; the relay gives its springs back. Everything else (a repair, a
    /// settings query, presence, pairing) is handled as usual.
    /// </summary>
    public static bool Refuses(string? messageType) =>
        messageType is MessageTypes.Unwind or MessageTypes.Call or MessageTypes.OwnerSettingsUpdate or MessageTypes.Wind;

    /// <summary>
    /// Whether <c>/windup safeword &lt;rest&gt;</c> asks to end the pause. A doll whose usable safeword is "resume" uses
    /// it instead: using the safeword favours the doll, and she can still end the pause with the config button.
    /// </summary>
    public static bool IsResume(string? rest, bool safewordUsable, string? storedSafeword) =>
        string.Equals(rest?.Trim(), ResumeWord, StringComparison.OrdinalIgnoreCase)
        && !(safewordUsable && SafewordText.Matches(rest, storedSafeword));
}
