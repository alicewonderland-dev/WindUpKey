#if WINDUP_TESTING || WINDUP_CALL_UNITS
using System;

namespace WindUpKey.Services;

/// <summary>Where an answered Call is. <c>CallTravelService</c> changes it only through its <c>SetPhase</c> (07-A1).</summary>
public enum CallPhase
{
    Idle,

    /// <summary>
    /// Before travel: waiting for her answer (07-B1, outside Hardcore) and for a duty, combat, crafting or another plugin's
    /// travel to end.
    /// </summary>
    WaitingGates,

    /// <summary>07-B1: the failed prompt, waiting for her retry (or her Decline).</summary>
    WaitingAccept,

    ChangingWorld,
    Teleporting,
    Pathing,
}

/// <summary>The Call's movement-lock bypass and input mute, as <see cref="CallLockPolicy.For"/> decides them.</summary>
public readonly record struct CallLockState(bool Bypass, bool Mute);

/// <summary>
/// 07-A1: the Call bypass (the unwound lock lifted) and the input mute are on only while the Call drives the doll, in
/// the travel phases. While it waits (a duty, combat, crafting, her answer, the failed prompt) an unwound doll stays
/// locked. The one exception is the landing grace: leaving a travel phase in the air keeps the bypass (not the mute)
/// until she has landed. No Dalamud dependency.
/// </summary>
public static class CallLockPolicy
{
    /// <summary>The phases that drive the character (a world change, a teleport or Lifestream trip, vnav pathing).</summary>
    public static bool IsTravelPhase(CallPhase phase) =>
        phase is CallPhase.ChangingWorld or CallPhase.Teleporting or CallPhase.Pathing;

    /// <summary>The bypass and mute for <paramref name="phase"/>; <paramref name="landing"/> is an active landing grace.</summary>
    public static CallLockState For(CallPhase phase, bool landing) =>
        IsTravelPhase(phase) ? new CallLockState(true, true) : new CallLockState(landing, false);
}

/// <summary>
/// 07-A1: a Call that leaves a travel phase while the doll is flying or diving keeps the bypass until she has landed,
/// for at most <see cref="MaxMs"/>, so she isn't locked in the air. Once she lands (or the time is up) it is over for
/// good: taking off again doesn't bring it back. Monotonic milliseconds from an injected clock. No Dalamud dependency.
/// </summary>
public sealed class CallLandingGrace(Func<long> clock)
{
    /// <summary>The longest the bypass is kept for a landing.</summary>
    public const long MaxMs = 60_000;

    private long? _startedMs;

    /// <summary>True while the grace holds (as of the last <see cref="Begin"/> or <see cref="Update"/>).</summary>
    public bool IsActive => _startedMs is not null;

    /// <summary>Leaving a travel phase: starts the grace when <paramref name="airborne"/>, else ends any earlier one.</summary>
    public void Begin(bool airborne) => _startedMs = airborne ? clock() : null;

    /// <summary>Back in a travel phase, or a cancel that needs no landing (unload): no grace.</summary>
    public void End() => _startedMs = null;

    /// <summary>
    /// 07-A10: each tick with no Call left. True exactly once, on the tick an active grace ends (she has landed, or
    /// <see cref="MaxMs"/> has passed), when the caller must take the bypass off; false while it holds or when there is none.
    /// </summary>
    public bool ReleaseBypass(bool airborne) => IsActive && !Update(airborne);

    /// <summary>Each tick: true while she is still <paramref name="airborne"/> within <see cref="MaxMs"/>; false once over.</summary>
    public bool Update(bool airborne)
    {
        if (_startedMs is not { } started)
            return false;

        if (!airborne || clock() - started >= MaxMs)
        {
            _startedMs = null;
            return false;
        }

        return true;
    }
}
#endif
