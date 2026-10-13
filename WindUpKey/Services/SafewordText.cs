using System;

namespace WindUpKey.Services;

/// <summary>
/// 06-A4: how the safeword is stored and compared. It is trimmed when stored and when compared, and an entry that is
/// empty once trimmed keeps the old safeword. The comparison stays case-sensitive. No Dalamud dependency.
/// </summary>
public static class SafewordText
{
    /// <summary>The safeword of a new profile, and of a stored one that is empty.</summary>
    public const string Default = "safeword";

    /// <summary>The safeword to store after the doll entered <paramref name="entered"/> over <paramref name="current"/>.</summary>
    public static string Store(string? entered, string? current)
    {
        var trimmed = entered?.Trim();
        return string.IsNullOrEmpty(trimmed) ? Normalize(current) : trimmed;
    }

    /// <summary>A stored safeword as it is compared: trimmed, or <see cref="Default"/> when empty.</summary>
    public static string Normalize(string? stored)
    {
        var trimmed = stored?.Trim();
        return string.IsNullOrEmpty(trimmed) ? Default : trimmed;
    }

    /// <summary>Whether <paramref name="typed"/> (from <c>/windup safeword</c>) is the stored safeword.</summary>
    public static bool Matches(string? typed, string? stored)
    {
        var trimmed = typed?.Trim();
        return !string.IsNullOrEmpty(trimmed) && string.Equals(trimmed, Normalize(stored), StringComparison.Ordinal);
    }
}
