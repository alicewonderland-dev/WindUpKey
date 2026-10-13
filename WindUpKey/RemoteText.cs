using System;
using System.Text;

namespace WindUpKey;

/// <summary>
/// Text that came from another player through the relay (03-D11). None of it is shown, stored or handed to another
/// plugin without going through here. No Dalamud dependency.
/// </summary>
public static class RemoteText
{
    /// <summary>
    /// Removes control characters (which include the game's text-payload markers), trims, and caps the length at
    /// <paramref name="max"/> UTF-16 code units without splitting a surrogate pair. Empty for null.
    /// </summary>
    public static string Sanitize(string? text, int max)
    {
        if (string.IsNullOrEmpty(text) || max <= 0)
            return string.Empty;

        var builder = new StringBuilder(Math.Min(text.Length, max + 16));
        foreach (var c in text)
        {
            if (!char.IsControl(c))
                builder.Append(c);
        }

        var clean = builder.ToString().Trim();
        if (clean.Length <= max)
            return clean;

        var cut = char.IsHighSurrogate(clean[max - 1]) ? max - 1 : max;
        return clean[..cut].TrimEnd();
    }

    /// <summary>
    /// A remote field that is either usable whole or not at all: sanitised when it is at most <paramref name="max"/>
    /// code units, otherwise empty, so the caller falls back to something it knows (03-D11: an over-long
    /// <c>worldName</c> is ignored in favour of the world ID, never truncated into another world's name).
    /// </summary>
    public static string WholeOrEmpty(string? text, int max) =>
        text is null || text.Trim().Length > max ? string.Empty : Sanitize(text, max);
}
