using System;

namespace WindUpKey.Ui;

/// <summary>
/// 06-A7: a confirmation that belongs to the character it was opened for, and for granting ownership also to the
/// partner it names. No Dalamud dependency.
/// </summary>
/// <param name="ContentId">The active profile (ContentId hex) when it was opened.</param>
/// <param name="PartnerKey">The normalized partner key it is about, or null (the Hardcore confirm).</param>
public sealed record CharacterBoundConfirm(string ContentId, string? PartnerKey)
{
    /// <summary>
    /// Opens a confirmation for <paramref name="activeContentId"/>, or null when no character is active (nothing to
    /// confirm for).
    /// </summary>
    public static CharacterBoundConfirm? Open(string? activeContentId, string? partnerKey = null) =>
        string.IsNullOrEmpty(activeContentId) ? null : new CharacterBoundConfirm(activeContentId, partnerKey);

    /// <summary>
    /// Whether the confirmation may still be acted on: the same character is active and still a doll, and the partner
    /// it names (if any) is still paired with it. Refused after a character switch, a role change or an unpair; the
    /// caller then closes it without doing anything.
    /// </summary>
    /// <param name="activeContentId">The active profile now.</param>
    /// <param name="isDoll">Whether the active profile is a doll now.</param>
    /// <param name="isPaired">Whether a partner key is paired with the active profile now.</param>
    public bool StillValid(string? activeContentId, bool isDoll, Func<string, bool> isPaired) =>
        isDoll
        && string.Equals(ContentId, activeContentId, StringComparison.Ordinal)
        && (PartnerKey is null || isPaired(PartnerKey));
}
