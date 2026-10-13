using System;

namespace WindUpKey.Services;

/// <summary>What cancelling the looping emote does on this tick (<see cref="EmoteCancelPlan.Next"/>).</summary>
public enum EmoteCancelAction
{
    /// <summary>Nothing this tick.</summary>
    None,

    /// <summary>Run <c>/sit</c>, which toggles a sit or groundsit off with the get-up animation.</summary>
    Sit,

    /// <summary>Send the game's interrupt-emote command (<see cref="EmoteCancelStep.Command"/>: 502 or 503).</summary>
    Interrupt,
}

/// <summary>Why a plan ended without the doll standing, for one log line.</summary>
public enum EmoteCancelNote
{
    None,

    /// <summary>The interrupt command isn't available this patch (null address): the doll stays posed until she moves.</summary>
    Unavailable,

    /// <summary>Both interrupt codes were sent and the doll is still in the loop.</summary>
    GaveUp,
}

/// <summary>One tick's step: what to do, the interrupt code for <see cref="EmoteCancelAction.Interrupt"/>, and a note.</summary>
public readonly record struct EmoteCancelStep(EmoteCancelAction Action, int Command = 0, EmoteCancelNote Note = EmoteCancelNote.None)
{
    public static readonly EmoteCancelStep Nothing = new(EmoteCancelAction.None);
}

/// <summary>What the game says on this tick, read by <c>LockController</c> on the framework thread.</summary>
/// <param name="Ready">Logged in, a local player, and not between areas: a command can be run.</param>
/// <param name="Locked">The doll is locked (unwound) again: the lock emote must not be cancelled.</param>
/// <param name="SitPose">In a sit or groundsit (InThatPosition and a sit pose): <c>/sit</c> stands her.</param>
/// <param name="InLoop">In a looping emote or pose (<c>EmoteController.IsInEmoteLoop</c> or InPositionLoop, or the old condition check).</param>
/// <param name="PositionLoop">The character's mode is InPositionLoop (doze-like poses).</param>
/// <param name="CommandAvailable"><c>GameMain.ExecuteCommand</c>'s address is non-null.</param>
public readonly record struct EmoteCancelFacts(
    bool Ready,
    bool Locked,
    bool SitPose,
    bool InLoop,
    bool PositionLoop,
    bool CommandAvailable);

/// <summary>
/// 06-C1: how a wound doll is stood up out of a looping emote, as a Dalamud-free rule; it replaces the forward-walk
/// nudge. A sit or groundsit gets <c>/sit</c> (it toggles, with the get-up animation). Any other loop gets the game's
/// own interrupt-emote command, what the client sends when you walk out of an emote: 503 (InterruptEmoteSpecial) for
/// a doze-like InPositionLoop pose, 502 (InterruptEmote) otherwise. If she is still in the loop about
/// <see cref="RetryAfterMs"/> ms later the other code is tried once, then the plan gives up. 06-C3: the wait is wall
/// time from a monotonic clock the caller passes (<c>Environment.TickCount64</c>), not frames: at 144–240 fps 30 frames
/// is shorter than one server round trip. Only time between consecutive ready ticks counts. When the command
/// isn't available (a null address after a patch) nothing is sent and the doll stays posed until she moves: a null
/// address never produces 502 or 503. Being locked again drops the plan. One request, ticked on the framework thread.
/// </summary>
public sealed class EmoteCancelPlan
{
    /// <summary>The client trigger the game sends when you walk out of an ordinary emote loop.</summary>
    public const int InterruptEmote = 502;

    /// <summary>The client trigger for chair-sit, doze and similar position loops.</summary>
    public const int InterruptEmoteSpecial = 503;

    /// <summary>Ready milliseconds after an interrupt before the other code is tried (and, after that one, before giving up).</summary>
    public const long RetryAfterMs = 1000;

    private enum Phase
    {
        Idle,
        Requested,
        FirstSent,
        SecondSent,
    }

    private Phase _phase;
    private int _firstCommand;
    private long _readyMsSinceSend;

    /// <summary>The clock at the last ready tick since the send; null after a not-ready tick, so that gap isn't counted.</summary>
    private long? _lastReadyMs;

    /// <summary>A request is waiting or in progress.</summary>
    public bool Pending => _phase != Phase.Idle;

    /// <summary>Start (or restart) cancelling: the next ready tick decides what to send.</summary>
    public void Request()
    {
        _phase = Phase.Requested;
        ResetWait(null);
    }

    /// <summary>Drop any request (logout, unload).</summary>
    public void Cancel()
    {
        _phase = Phase.Idle;
        ResetWait(null);
    }

    private void ResetWait(long? nowMs)
    {
        _readyMsSinceSend = 0;
        _lastReadyMs = nowMs;
    }

    /// <summary>The step for this tick, given what the game says now and a monotonic clock in milliseconds.</summary>
    public EmoteCancelStep Next(EmoteCancelFacts facts, long nowMs)
    {
        if (_phase == Phase.Idle)
            return EmoteCancelStep.Nothing;

        if (facts.Locked)
        {
            Cancel();
            return EmoteCancelStep.Nothing;
        }

        // Zoning or no player yet: wait without counting, so the retry timing only covers time she could stand in.
        if (!facts.Ready)
        {
            _lastReadyMs = null;
            return EmoteCancelStep.Nothing;
        }

        if (_phase == Phase.Requested)
        {
            if (facts.SitPose)
            {
                Cancel();
                return new EmoteCancelStep(EmoteCancelAction.Sit);
            }

            if (!facts.InLoop)
            {
                Cancel();
                return EmoteCancelStep.Nothing;
            }

            if (!facts.CommandAvailable)
            {
                Cancel();
                return new EmoteCancelStep(EmoteCancelAction.None, Note: EmoteCancelNote.Unavailable);
            }

            _firstCommand = facts.PositionLoop ? InterruptEmoteSpecial : InterruptEmote;
            _phase = Phase.FirstSent;
            ResetWait(nowMs);
            return new EmoteCancelStep(EmoteCancelAction.Interrupt, _firstCommand);
        }

        // An interrupt was sent: out of the loop means it worked.
        if (!facts.InLoop)
        {
            Cancel();
            return EmoteCancelStep.Nothing;
        }

        if (_lastReadyMs is { } lastReadyMs)
            _readyMsSinceSend += Math.Max(0, nowMs - lastReadyMs);
        _lastReadyMs = nowMs;
        if (_readyMsSinceSend < RetryAfterMs)
            return EmoteCancelStep.Nothing;

        if (_phase == Phase.FirstSent && facts.CommandAvailable)
        {
            _phase = Phase.SecondSent;
            ResetWait(nowMs);
            return new EmoteCancelStep(
                EmoteCancelAction.Interrupt,
                _firstCommand == InterruptEmote ? InterruptEmoteSpecial : InterruptEmote);
        }

        Cancel();
        return new EmoteCancelStep(EmoteCancelAction.None, Note: EmoteCancelNote.GaveUp);
    }
}

/// <summary>
/// 06-C1: when <c>LockController</c>'s movement hooks are needed. Only while locked or input-muted (Call travel):
/// cancelling a pose no longer walks the doll forward, so nothing keeps the hooks alive for it. No Dalamud dependency.
/// </summary>
public static class MovementHookRule
{
    public static bool Needed(bool locked, bool inputMute) => locked || inputMute;
}
