#if WINDUP_TESTING || WINDUP_SELFTEST_UNITS
using System;
using System.Collections.Generic;
using System.Linq;

namespace WindUpKey.SelfTest;

/// <summary>
/// 04-D3 redaction: keys only as <c>AB******</c>, hosts only as the host name, files only by their names under
/// <c>pluginConfigs</c> (never a full path), and a final scrubber that replaces any occurrence of a sensitive value
/// (secret, previous secret, their hashes, the relay token, the ContentId, full keys) with <c>[redacted]</c> before
/// anything is written. No Dalamud dependency.
/// </summary>
public static class SelfTestRedact
{
    public const string Redacted = "[redacted]";

    /// <summary>
    /// Values shorter than this aren't scrubbed: replacing a 1–3 character value would shred ordinary words. Every real
    /// sensitive value is far longer (keys 8, ContentIds 16, secrets 43, hashes 64; the compiled token is long).
    /// </summary>
    public const int MinScrubLength = 4;

    /// <summary>A key as <c>AB******</c>: its first two characters and six stars, whatever its length (<see cref="LogRedact.Key"/>).</summary>
    public static string Key(string? key) => LogRedact.Key(key);

    /// <summary>Only the host name of a relay URL, or <c>(invalid-url)</c>.</summary>
    public static string Host(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return "(invalid-url)";
        return uri.Host;
    }

    /// <summary>
    /// Replaces every occurrence of each sensitive value (case-insensitively, longest first, so a value containing
    /// another is replaced whole) with <see cref="Redacted"/>. Values that are blank or shorter than
    /// <see cref="MinScrubLength"/> are skipped.
    /// </summary>
    public static string Scrub(string text, IEnumerable<string?> sensitive)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        foreach (var value in sensitive
                     .Where(v => !string.IsNullOrWhiteSpace(v) && v!.Trim().Length >= MinScrubLength)
                     .Select(v => v!.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(v => v.Length))
        {
            text = text.Replace(value, Redacted, StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }
}
#endif
