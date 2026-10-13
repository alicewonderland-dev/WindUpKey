#if WINDUP_TESTING || WINDUP_CALL_UNITS
namespace WindUpKey.Services;

/// <summary>What keeps a Call from travelling right now, other than another plugin's travel (in priority order).</summary>
public enum CallGate
{
    None,
    Crafting,

    /// <summary>A duty, or a zone load.</summary>
    Instance,

    Combat,

    /// <summary>Lifestream or vnavmesh isn't ready.</summary>
    NotReady,
}

/// <summary>What a waiting Call does this tick, as <see cref="CallAskFirst.Decide"/> rules it.</summary>
public enum CallStart
{
    /// <summary>Keep waiting (for her answer, a gate, or her retry after a failure).</summary>
    Wait,

    /// <summary>Start travelling.</summary>
    Travel,

    /// <summary>Her own Lifestream or vnav trip is running: stop it, then start travelling.</summary>
    StopHerTravelThenTravel,
}

/// <summary>07-B6: a waiting Call after her Accept: whether it is accepted now, and its phase (WaitingGates after a retry).</summary>
public readonly record struct CallAcceptStep(bool Accepted, CallPhase Phase);

/// <summary>
/// 07-B1 (D3, D5), 07-B2: when an answered Call may travel. A non-Hardcore doll is asked first: nothing moves before her
/// Accept, which she may give while a gate holds (it's remembered, and travel starts when the gate clears, with no
/// second prompt). A Hardcore doll travels on her own once the gates clear, and can't decline or stop. Her own trip
/// (another plugin's travel) is never a reason to wait once she has accepted (or in Hardcore): it is stopped then, and
/// only then. After a failure, every doll is asked again (the failed prompt's Accept), Hardcore too: a retry is no way
/// out, and the failed-prompt limit still ends it. No Dalamud dependency.
/// </summary>
public static class CallAskFirst
{
    /// <summary>07-B2: Lifestream or vnav still busy this soon after our own abort is our automation draining, not hers.</summary>
    public const long OwnDrainMs = 5_000;

    /// <summary>07-B1 (D5): the chat line when Hardcore refuses <c>/windup stopcall</c>.</summary>
    public const string HardcoreStopRefusal = "Hardcore: you can't stop a Call.";

    /// <summary>
    /// What a waiting Call does: <paramref name="accepted"/> is her remembered Accept, <paramref name="failedPrompt"/>
    /// the failed prompt waiting for her retry, <paramref name="gate"/> the first gate that holds, and
    /// <paramref name="herTravelBusy"/> another plugin's travel (not ours, not our own draining).
    /// </summary>
    public static CallStart Decide(bool hardcore, bool accepted, bool failedPrompt, CallGate gate, bool herTravelBusy)
    {
        if (failedPrompt)
            return CallStart.Wait;
        if (!hardcore && !accepted)
            return CallStart.Wait;
        if (gate != CallGate.None)
            return CallStart.Wait;
        return herTravelBusy ? CallStart.StopHerTravelThenTravel : CallStart.Travel;
    }

    /// <summary>D5: Decline, Stop and <c>/windup stopcall</c> exist only outside Hardcore.</summary>
    public static bool MayDecline(bool hardcore) => !hardcore;

    /// <summary>The prompt offers Accept: a non-Hardcore Call she hasn't accepted yet, or any failed prompt (a retry).</summary>
    public static bool OffersAccept(bool hardcore, bool accepted, bool failedPrompt) =>
        failedPrompt || (!hardcore && !accepted);

    /// <summary>
    /// 07-B6: what her Accept does to a waiting Call in <paramref name="phase"/> (WaitingGates, or WaitingAccept: the
    /// failed prompt). Where the prompt offers Accept it is remembered; on the failed prompt it is the retry, back to
    /// WaitingGates and no longer failed (the caller first stops the failed attempt's draining automation). Anywhere
    /// else (Hardcore before a failure, or an Accept already given) it changes nothing.
    /// </summary>
    public static CallAcceptStep OnAccept(bool hardcore, bool accepted, CallPhase phase)
    {
        var failedPrompt = phase == CallPhase.WaitingAccept;
        if (!OffersAccept(hardcore, accepted, failedPrompt))
            return new CallAcceptStep(accepted, phase);
        return new CallAcceptStep(Accepted: true, failedPrompt ? CallPhase.WaitingGates : phase);
    }

    /// <summary>
    /// 07-B2, 07-B6: whether this tick reads her own travel (<c>IsHerTravelBusy</c>, which aborts our own draining
    /// automation): only once no gate holds, and never on the failed prompt, where a trip she starts herself right after
    /// a failure (within <see cref="OwnDrainMs"/> of our abort) must not be aborted without her Accept.
    /// </summary>
    public static bool ReadsHerTravel(bool failedPrompt, CallGate gate) => !failedPrompt && gate == CallGate.None;

    /// <summary>
    /// D4: a Call from an owner in <paramref name="quiet"/> (her quiet period after a Decline) is declined straight away,
    /// but not in Hardcore, where she can't decline (D5).
    /// </summary>
    public static bool AutoDeclines(bool hardcore, bool quiet) => quiet && !hardcore;

    /// <summary>07-B2: whether busy travel now is still our own automation draining after our abort at <paramref name="lastOwnAbortMs"/>.</summary>
    public static bool IsOwnDrain(long? lastOwnAbortMs, long nowMs) =>
        lastOwnAbortMs is { } abortedAt && nowMs - abortedAt < OwnDrainMs;
}
#endif
