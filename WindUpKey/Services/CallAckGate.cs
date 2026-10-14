using System;

namespace WindUpKey.Services;

/// <summary>
/// 07-A2: the doll's Call acks. A status is sent only when it differs from the last one sent, and at most one every
/// <see cref="FloorMs"/>; a change inside that floor is held and sent by <see cref="Flush"/> once the floor has passed
/// (only the latest held status). Before this, a Call during a duty or a zone load acked every tick, hit the relay's
/// rate limit, and her <c>callResult</c> was refused too. <see cref="Reset"/> for each Call. Monotonic milliseconds from
/// an injected clock. No Dalamud dependency.
/// </summary>
public sealed class CallAckGate(Func<long> clock)
{
    /// <summary>At most one ack in this many milliseconds.</summary>
    public const long FloorMs = 2_000;

    private string? _lastSent;
    private long _lastSentMs;
    private string? _held;

    /// <summary>A new Call: nothing sent yet.</summary>
    public void Reset()
    {
        _lastSent = null;
        _lastSentMs = 0;
        _held = null;
    }

    /// <summary>The status to send now, or null (unchanged, or held until the floor has passed).</summary>
    public string? Offer(string status)
    {
        if (string.Equals(status, _lastSent, StringComparison.Ordinal))
        {
            // Back to what was sent last: a held change no longer needs sending.
            _held = null;
            return null;
        }

        if (_lastSent is not null && clock() - _lastSentMs < FloorMs)
        {
            _held = status;
            return null;
        }

        return MarkSent(status);
    }

    /// <summary>Each tick: the held status once the floor has passed, else null.</summary>
    public string? Flush()
    {
        if (_held is not { } held || clock() - _lastSentMs < FloorMs)
            return null;

        return MarkSent(held);
    }

    private string MarkSent(string status)
    {
        _lastSent = status;
        _lastSentMs = clock();
        _held = null;
        return status;
    }
}
