#if WINDUP_TESTING || WINDUP_CALL_UNITS
using System;
using System.Collections.Generic;

namespace WindUpKey.Services;

/// <summary>
/// 07-A3 (D7): the owner's Calls still waiting for a reply, by request id. An entry is forgotten after
/// <see cref="TtlMs"/>, at most <see cref="MaxEntries"/> are kept (the oldest dropped), it is pruned on every add and
/// lookup, and each id is taken once. A Release doll never replies, so before this the set only grew. A late reply for
/// a forgotten id falls through to the generic handling. Thread-safe: sends come from Draw, replies from the tick.
/// Monotonic milliseconds from an injected clock. No Dalamud dependency.
/// </summary>
public sealed class PendingCallBook(Func<long> clock)
{
    /// <summary>How long a Call waits for its reply.</summary>
    public const long TtlMs = 30 * 60_000;

    /// <summary>The most Calls kept waiting.</summary>
    public const int MaxEntries = 32;

    private readonly object _gate = new();

    // Oldest first: ids are added with the clock, which never goes back.
    private readonly LinkedList<(string Id, long AddedMs)> _order = new();
    private readonly Dictionary<string, LinkedListNode<(string Id, long AddedMs)>> _byId = new(StringComparer.Ordinal);

    /// <summary>How many Calls are waiting (after pruning).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                Prune(clock());
                return _byId.Count;
            }
        }
    }

    /// <summary>Remembers a sent Call.</summary>
    public void Add(string requestId)
    {
        if (string.IsNullOrEmpty(requestId))
            return;

        lock (_gate)
        {
            var now = clock();
            Prune(now);
            if (_byId.Remove(requestId, out var existing))
                _order.Remove(existing);

            _byId[requestId] = _order.AddLast((requestId, now));
            while (_byId.Count > MaxEntries && _order.First is { } oldest)
            {
                _order.RemoveFirst();
                _byId.Remove(oldest.Value.Id);
            }
        }
    }

    /// <summary>True (once) when <paramref name="requestId"/> is a Call still waiting; it is forgotten then.</summary>
    public bool TryTake(string? requestId)
    {
        if (string.IsNullOrEmpty(requestId))
            return false;

        lock (_gate)
        {
            Prune(clock());
            if (!_byId.Remove(requestId, out var node))
                return false;

            _order.Remove(node);
            return true;
        }
    }

    private void Prune(long now)
    {
        while (_order.First is { } oldest && now - oldest.Value.AddedMs >= TtlMs)
        {
            _order.RemoveFirst();
            _byId.Remove(oldest.Value.Id);
        }
    }
}
#endif
