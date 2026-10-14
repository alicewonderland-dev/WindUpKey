using System;
using System.Collections.Generic;
using WindUpKey.Protocol;

namespace WindUpKey.Services;

/// <summary>
/// 07-B1 (D4): after the doll declines (or stops) an owner's Call, that owner's Calls are answered
/// <see cref="CallResultStatuses.Declined"/> straight away for <see cref="DurationMs"/>. Per owner key, in memory only
/// (no config field: a reload forgets it). Only her own Decline starts it: a cancel (the safeword, logout, consent lost),
/// a failure, an expiry or an arrival never does, and an automatic decline doesn't extend it. Framework thread.
/// Monotonic milliseconds from an injected clock. No Dalamud dependency.
/// </summary>
public sealed class CallQuietPeriod(Func<long> clock)
{
    /// <summary>How long an owner's Calls are declined automatically after her Decline.</summary>
    public const long DurationMs = 5 * 60_000;

    private const long MinuteMs = 60_000;

    private readonly Dictionary<string, long> _declinedAtMs = new(StringComparer.Ordinal);

    /// <summary>
    /// A Call from <paramref name="ownerKey"/> ended with <paramref name="status"/>. Only
    /// <see cref="CallResultStatuses.Declined"/> starts (or restarts) the quiet period; anything else leaves it alone.
    /// </summary>
    public void Record(string ownerKey, string status)
    {
        if (string.IsNullOrEmpty(ownerKey) || !string.Equals(status, CallResultStatuses.Declined, StringComparison.Ordinal))
            return;

        var now = clock();
        Prune(now);
        _declinedAtMs[ownerKey] = now;
    }

    /// <summary>
    /// True while <paramref name="ownerKey"/>'s Calls are declined automatically, with the whole minutes left rounded up
    /// (1 to 5). It ends exactly <see cref="DurationMs"/> after the Decline.
    /// </summary>
    public bool IsQuiet(string ownerKey, out int minutesLeft)
    {
        minutesLeft = 0;
        if (string.IsNullOrEmpty(ownerKey))
            return false;

        var now = clock();
        Prune(now);
        if (!_declinedAtMs.TryGetValue(ownerKey, out var declinedAt))
            return false;

        var left = DurationMs - (now - declinedAt);
        minutesLeft = (int)((left + MinuteMs - 1) / MinuteMs);
        return true;
    }

    private void Prune(long now)
    {
        List<string>? over = null;
        foreach (var (key, declinedAt) in _declinedAtMs)
        {
            if (now - declinedAt >= DurationMs)
                (over ??= []).Add(key);
        }

        if (over is null)
            return;

        foreach (var key in over)
            _declinedAtMs.Remove(key);
    }
}

/// <summary>07-B1 (D4): what a new owner client shows for a Call's result. No Dalamud dependency.</summary>
public static class CallResultNotice
{
    /// <summary>
    /// The owner's chat line for <paramref name="status"/> from the doll labelled <paramref name="dollLabel"/>: only a
    /// Decline has one. Every other status (arrived, failed, cancelled, or one this build doesn't know) is null and stays
    /// silent, as before.
    /// </summary>
    public static string? ForOwner(string? status, string dollLabel) =>
        string.Equals(status, CallResultStatuses.Declined, StringComparison.Ordinal)
            ? $"{dollLabel} declined your Call."
            : null;
}
