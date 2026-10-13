using System;

namespace WindUpKey.Services;

/// <summary>
/// 07-A2: a chat notice shown at most once per interval, such as the relay's "busy" line, which a burst of refused
/// messages used to print once each. Monotonic milliseconds from an injected clock. No Dalamud dependency.
/// </summary>
public sealed class NoticeThrottle(long intervalMs, Func<long> clock)
{
    private long? _lastShownMs;

    /// <summary>True when the notice may be shown now (and counts it as shown).</summary>
    public bool TryShow()
    {
        var now = clock();
        if (_lastShownMs is { } last && now - last < intervalMs)
            return false;

        _lastShownMs = now;
        return true;
    }
}
