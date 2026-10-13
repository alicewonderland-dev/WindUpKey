#if WINDUP_TESTING || WINDUP_SELFTEST_UNITS
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using WindUpKey.Protocol;

namespace WindUpKey.SelfTest;

/// <summary>What the receive loop does with an inbound envelope while the self-test runs (04-D6).</summary>
public enum TapVerdict
{
    /// <summary>Not the self-test's: normal handling.</summary>
    None,

    /// <summary>The self-test's reply: it never reaches normal handling, so no chat line can appear.</summary>
    Consume,

    /// <summary>A real partner's presence answer to a self-test query: recorded, and normal handling still runs.</summary>
    Observe,
}

/// <summary>
/// Matches the live session's replies to the self-test's requests (04-D6). Request ids are <c>st-&lt;guid N&gt;</c>, each
/// with its own timeout. The receive loop calls <see cref="Inspect"/> before handing an envelope to the framework
/// thread:
/// <list type="bullet">
/// <item><b>Consume</b>: an <c>error</c> or <c>presenceResult</c> with a self-test id, or a <c>pong</c> while one of the
/// self-test's pings is outstanding;</item>
/// <item><b>Observe</b>: a <c>presenceResult</c> with a self-test id from a partner that query went to;</item>
/// <item><b>None</b>: everything else.</item>
/// </list>
/// Ids stay known for the whole run, so a late or second answer is matched the same way. Thread-safe. No Dalamud
/// dependency.
/// </summary>
public sealed class SelfTestTap
{
    public const string RequestIdPrefix = "st-";

    private readonly object _gate = new();
    private readonly Dictionary<string, Expectation> _byId = new(StringComparer.Ordinal);
    private readonly Queue<TaskCompletionSource<Envelope?>> _pings = new();

    private sealed record Expectation(TaskCompletionSource<Envelope?> Reply, IReadOnlySet<string> ObservedFrom);

    /// <summary>A new self-test request id.</summary>
    public static string NewRequestId() => RequestIdPrefix + Guid.NewGuid().ToString("N");

    /// <summary>True for ids the self-test makes.</summary>
    public static bool IsSelfTestId(string? requestId) =>
        requestId is not null && requestId.StartsWith(RequestIdPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Waits for the first reply to <paramref name="requestId"/> (null on timeout or cancel). Presence answers from a
    /// key in <paramref name="observedFrom"/> are observed rather than consumed.
    /// </summary>
    public Task<Envelope?> ExpectAsync(string requestId, TimeSpan timeout, CancellationToken ct, IEnumerable<string>? observedFrom = null)
    {
        var reply = new TaskCompletionSource<Envelope?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var from = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in observedFrom ?? [])
            from.Add(WireLimits.NormalizeKey(key));
        lock (_gate)
            _byId[requestId] = new Expectation(reply, from);
        return WithTimeout(reply, timeout, ct);
    }

    /// <summary>Waits for a pong to the ping about to be sent (null on timeout or cancel).</summary>
    public Task<Envelope?> ExpectPongAsync(TimeSpan timeout, CancellationToken ct)
    {
        var reply = new TaskCompletionSource<Envelope?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
            _pings.Enqueue(reply);
        return WithTimeout(reply, timeout, ct);
    }

    /// <summary>
    /// The verdict for one inbound envelope (see the class summary), completing the matching wait. Never throws: a
    /// payload it can't read is <see cref="TapVerdict.None"/> unless its type alone decides.
    /// </summary>
    public TapVerdict Inspect(Envelope envelope)
    {
        switch (envelope.Type)
        {
            case MessageTypes.Pong:
                lock (_gate)
                {
                    while (_pings.Count > 0)
                    {
                        // A ping that already timed out doesn't own this pong; the next outstanding one may.
                        if (_pings.Dequeue().TrySetResult(envelope))
                            return TapVerdict.Consume;
                    }
                }

                return TapVerdict.None;

            case MessageTypes.Error:
            case MessageTypes.PresenceResult:
                var id = ReadString(envelope.Payload, "requestId");
                if (!IsSelfTestId(id))
                    return TapVerdict.None;

                Expectation? expectation;
                lock (_gate)
                    _byId.TryGetValue(id!, out expectation);
                expectation?.Reply.TrySetResult(envelope);

                if (envelope.Type == MessageTypes.PresenceResult
                    && expectation is not null
                    && expectation.ObservedFrom.Contains(WireLimits.NormalizeKey(ReadString(envelope.Payload, "from"))))
                    return TapVerdict.Observe;

                // Ours (even late or unknown): never normal handling, so no chat line.
                return TapVerdict.Consume;

            default:
                return TapVerdict.None;
        }
    }

    /// <summary>
    /// The verdict when no run is active (<paramref name="tap"/> null): a late <c>error</c> with a self-test id is still
    /// consumed, so a reply that arrives after the run can't print the generic failure line. Everything else is normal.
    /// </summary>
    public static TapVerdict Inspect(SelfTestTap? tap, Envelope envelope)
    {
        if (tap is not null)
            return tap.Inspect(envelope);
        return envelope.Type == MessageTypes.Error && IsSelfTestId(ReadString(envelope.Payload, "requestId"))
            ? TapVerdict.Consume
            : TapVerdict.None;
    }

    /// <summary>Ends every wait (the run is over or stopped).</summary>
    public void CancelAll()
    {
        lock (_gate)
        {
            foreach (var expectation in _byId.Values)
                expectation.Reply.TrySetResult(null);
            while (_pings.Count > 0)
                _pings.Dequeue().TrySetResult(null);
        }
    }

    private static async Task<Envelope?> WithTimeout(TaskCompletionSource<Envelope?> reply, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            return await reply.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            reply.TrySetResult(null);
            return null;
        }
        catch (OperationCanceledException)
        {
            reply.TrySetResult(null);
            throw;
        }
    }

    private static string? ReadString(JsonObject? payload, string name)
    {
        try
        {
            return payload?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
#endif
