using System.Collections.Generic;

namespace WindUpKey.Ui;

/// <summary>What the doll's "Unwound emote" combo shows for the stored lock emote.</summary>
/// <param name="SelectedId">The stored emote (0 read as Ground Sit), always: the combo never swaps it for another.</param>
/// <param name="Preview">The combo's preview text.</param>
/// <param name="Unlocked">Whether the emote is in this character's unlocked looping emotes.</param>
public readonly record struct LockEmoteView(ushort SelectedId, string Preview, bool Unlocked);

/// <summary>
/// 06-A1: the display rule for the doll's own lock emote combo. It shows the stored emote even when this character
/// hasn't unlocked it, with a note that Ground Sit is used instead (the game already falls back at runtime, in
/// <c>GameCommandRunner.ResolveLockEmoteId</c>). It only describes what to draw and never asks for a write: opening the
/// config on an alt no longer resets the setting. No Dalamud dependency.
/// </summary>
public static class LockEmoteDisplay
{
    /// <summary>Appended to the preview of an emote this character can't use as the unwound emote (not unlocked, or 06-C6 excluded).</summary>
    public const string GroundSitFallbackNote = " (not available on this character; Ground Sit is used)";

    /// <summary>
    /// The view of <paramref name="storedId"/> given this character's unlocked looping emotes
    /// (<paramref name="unlocked"/>) and the Emote sheet name (<paramref name="sheetName"/>, null when unknown).
    /// </summary>
    public static LockEmoteView Resolve(ushort storedId, IReadOnlyList<(ushort Id, string Name)> unlocked, string? sheetName)
    {
        var id = storedId == 0 ? CharacterProfile.GroundSitEmoteId : storedId;
        foreach (var (emoteId, name) in unlocked)
        {
            if (emoteId == id)
                return new LockEmoteView(id, name, Unlocked: true);
        }

        var display = string.IsNullOrWhiteSpace(sheetName)
            ? id == CharacterProfile.GroundSitEmoteId ? "Sit on Ground" : $"Emote #{id}"
            : sheetName;
        // Ground Sit is the fallback itself, so it never gets the note.
        return id == CharacterProfile.GroundSitEmoteId
            ? new LockEmoteView(id, display, Unlocked: true)
            : new LockEmoteView(id, display + GroundSitFallbackNote, Unlocked: false);
    }
}
