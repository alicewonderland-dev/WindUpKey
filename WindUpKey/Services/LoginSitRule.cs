namespace WindUpKey.Services;

/// <summary>What a pending login sit does on this tick (<see cref="LoginSitRule.Next"/>).</summary>
public enum LoginSitStep
{
    /// <summary>Nothing more to do: the doll isn't locked, is already posed, or the attempts ran out.</summary>
    Done,

    /// <summary>The game isn't ready for an emote yet (no local player, or between areas): try again next tick.</summary>
    Wait,

    /// <summary>Play the lock emote now.</summary>
    Sit,
}

/// <summary>
/// 06-A2 / 06-A12: the login sit (<see cref="WindTimerService"/>), as a Dalamud-free rule. Once the logged-in character's
/// profile is active, an unwound doll with the lock emote on is sat down, unless she is already in a pose or an emote
/// loop: then the sit would only toggle her out of it.
/// </summary>
public static class LoginSitRule
{
    /// <summary>About 30 s of ticks before a login sit that can't happen is given up.</summary>
    public const int MaxAttempts = 1800;

    /// <summary>
    /// The step for this tick. <paramref name="attempts"/> counts this tick's attempt (only counted while locked).
    /// </summary>
    public static LoginSitStep Next(bool locked, bool canEmote, bool alreadyPosed, int attempts)
    {
        if (!locked)
            return LoginSitStep.Done;

        if (!canEmote)
            return attempts >= MaxAttempts ? LoginSitStep.Done : LoginSitStep.Wait;

        return alreadyPosed ? LoginSitStep.Done : LoginSitStep.Sit;
    }

    /// <summary>After a <see cref="LoginSitStep.Sit"/>: whether the login sit is finished (it played, or ran out of attempts).</summary>
    public static bool FinishedAfterSit(bool played, int attempts) => played || attempts >= MaxAttempts;
}
