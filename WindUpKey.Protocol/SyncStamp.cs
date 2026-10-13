using System.Globalization;

namespace WindUpKey.Protocol;

/// <summary>
/// Last-write-wins stamps for synced consent and wind (<c>updatedUtc</c>, <c>windUpdatedUtc</c>). Written as
/// round-trip (<c>"O"</c>) values, and read the same way on relay and client whatever the machine's culture.
/// </summary>
public static class SyncStamp
{
    /// <summary>
    /// How far ahead of the reader's clock a stamp may be. Anything later would win last-write-wins until that
    /// time, so relay and client reject it.
    /// </summary>
    public static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Parses a stamp culture-invariantly: round-trip (<c>"O"</c>) first, then invariant general parsing for older
    /// stored values. A value without an offset counts as UTC either way, whatever the machine's time zone.
    /// </summary>
    public static bool TryParse(string? raw, out DateTimeOffset value)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = default;
            return false;
        }

        return DateTimeOffset.TryParseExact(raw, "O", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value)
               || DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);
    }

    /// <summary>True when <paramref name="stamp"/> is more than <see cref="MaxFutureSkew"/> after <paramref name="now"/>.</summary>
    public static bool IsTooFarAhead(DateTimeOffset stamp, DateTimeOffset now) => stamp - now > MaxFutureSkew;
}
