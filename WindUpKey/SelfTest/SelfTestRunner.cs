#if WINDUP_TESTING || WINDUP_SELFTEST_UNITS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WindUpKey.SelfTest;

/// <summary>
/// Runs self-test steps in order (Architecture): each with its own timeout, cancellable, an exception turned into a
/// FAIL (its type only: messages can hold URLs), and restores in <c>finally</c> that report a loud FAIL with recovery
/// steps when they fail. At the end, <see cref="ApplyInvariants"/> turns the session-dependent results into SKIP when
/// the live session changed underneath them (04-D6). No Dalamud dependency.
/// </summary>
public sealed class SelfTestRunner
{
    private readonly object _gate = new();
    private readonly List<CheckResult> _results = [];

    public IReadOnlyList<CheckResult> Results
    {
        get
        {
            lock (_gate)
                return _results.ToList();
        }
    }

    public void Add(CheckResult result)
    {
        lock (_gate)
            _results.Add(result);
    }

    public void AddRange(IEnumerable<CheckResult> results)
    {
        lock (_gate)
            _results.AddRange(results);
    }

    /// <summary>
    /// Runs one step and adds its results. A step that takes longer than <paramref name="timeout"/> is a FAIL for each
    /// of <paramref name="ids"/>, and its token is cancelled; one that throws is a FAIL with the exception's type. When
    /// <paramref name="ct"/> is cancelled this throws <see cref="OperationCanceledException"/> and adds nothing.
    /// </summary>
    public async Task<IReadOnlyList<CheckResult>> StepAsync(
        IReadOnlyList<string> ids,
        TimeSpan timeout,
        Func<CancellationToken, Task<IReadOnlyList<CheckResult>>> step,
        CancellationToken ct)
    {
        IReadOnlyList<CheckResult> results;
        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            results = await step(stepCts.Token).WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await stepCts.CancelAsync().ConfigureAwait(false);
            throw;
        }
        catch (TimeoutException)
        {
            await stepCts.CancelAsync().ConfigureAwait(false);
            results = ids.Select(id => CheckResult.Fail(id, $"timed out after {timeout.TotalSeconds:0} s")).ToList();
        }
        catch (Exception ex)
        {
            results = ids.Select(id => CheckResult.Fail(id, $"failed with {ex.GetType().Name}")).ToList();
        }

        AddRange(results);
        return results;
    }

    /// <summary>
    /// Runs a disruptive step (04-D7), then always runs <paramref name="restore"/> with its own timeout, even after a
    /// cancel or a failure. A restore that returns false, throws or times out adds a loud FAIL for <paramref name="id"/>
    /// with <paramref name="recovery"/>.
    /// </summary>
    public async Task RunWithRestoreAsync(
        string id,
        TimeSpan timeout,
        Func<CancellationToken, Task<IReadOnlyList<CheckResult>>> step,
        Func<CancellationToken, Task<bool>> restore,
        TimeSpan restoreTimeout,
        string recovery,
        CancellationToken ct)
    {
        try
        {
            await StepAsync([id], timeout, step, ct).ConfigureAwait(false);
        }
        finally
        {
            bool restored;
            using var restoreCts = new CancellationTokenSource(restoreTimeout);
            try
            {
                restored = await restore(restoreCts.Token).WaitAsync(restoreTimeout).ConfigureAwait(false);
            }
            catch (Exception)
            {
                restored = false;
            }

            if (!restored)
                Add(CheckResult.LoudFail(id, $"RESTORE FAILED. {recovery}"));
        }
    }

    /// <summary>
    /// 04-D6 session invariants: when <paramref name="changed"/> (from <see cref="SessionInvariants.Changed"/>) is set
    /// and not caused by an opt-in verb, every result for <paramref name="sessionDependent"/> that isn't already SKIP or
    /// MANUAL becomes SKIP with the reason, so a check never passes or fails on a different session. Always adds the
    /// <c>X-session</c> result.
    /// </summary>
    public void ApplyInvariants(string? changed, IReadOnlySet<string> sessionDependent, bool causedByVerb)
    {
        lock (_gate)
        {
            if (changed is null)
            {
                _results.Add(CheckResult.Pass("X-session", "the live session was the same before and after the run"));
                return;
            }

            if (causedByVerb)
            {
                _results.Add(CheckResult.Info("X-session", $"the live session changed, as the verb intends: {changed}"));
                return;
            }

            for (var i = 0; i < _results.Count; i++)
            {
                var result = _results[i];
                if (sessionDependent.Contains(result.Id) && result.Status is not (CheckStatus.Skip or CheckStatus.Manual) && !result.Loud)
                    _results[i] = CheckResult.Skip(result.Id, $"the live session changed during the run ({changed}); was {result.Status}: {result.Reason}");
            }

            _results.Add(CheckResult.Warn("X-session", $"the live session changed during the run: {changed}"));
        }
    }

    /// <summary>
    /// 04-D10 verb invariants: a verb reconnects, so a new connection is expected (INFO), but a new auth block or a
    /// <c>session_replaced</c> isn't something it causes: that is a WARN. Always adds the <c>X-session</c> result.
    /// </summary>
    public void ApplyVerbInvariants(SessionInvariants before, SessionInvariants after)
    {
        if (SessionInvariants.NotCausedByVerb(before, after) is { } unexplained)
        {
            Add(CheckResult.Warn("X-session", $"the live session changed in a way the verb doesn't cause: {unexplained}"));
            return;
        }

        ApplyInvariants(SessionInvariants.Changed(before, after), new HashSet<string>(), causedByVerb: true);
    }
}
#endif
