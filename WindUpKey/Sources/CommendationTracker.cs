using System;

namespace WindUpKey.Sources;

/// <summary>A commendation bonus earned by an observed duty (<see cref="CommendationTracker.Update"/>).</summary>
/// <param name="Bonus">The wind to add (<see cref="CommendationTracker.CalculateBonus"/> of the observed duration).</param>
/// <param name="CommendationsReceived">How many commendations arrived in the first increase.</param>
/// <param name="ObservedDuration">How long the duty was observed, from its start (or first sight) to completion.</param>
/// <param name="Eligible">Whether the character stayed a doll throughout; otherwise nothing is added.</param>
public readonly record struct CommendationAward(
    TimeSpan Bonus,
    int CommendationsReceived,
    TimeSpan ObservedDuration,
    bool Eligible);

/// <summary>
/// 06-A5: when a duty earns a commendation bonus, without Dalamud. The clock is injected: a monotonic elapsed time.
/// <list type="bullet">
/// <item>A duty is observed from its start event, or from the first frame it is seen started (join in progress,
/// reconnect, plugin load), and is armed once it completes.</item>
/// <item>A completed duty stays armed for at most <see cref="ArmedTimeout"/>; then it resets, so a later commendation
/// can't be credited to it.</item>
/// <item>A duty seen starting while armed (an event, or a join in progress) restarts observation: a bonus is only
/// ever measured on the duty that earned the commendation.</item>
/// <item>At most one award per duty: the first commendation increase is consumed once, and the rest of that duty is
/// ignored.</item>
/// </list>
/// Framework thread only (not thread-safe).
/// </summary>
public sealed class CommendationTracker
{
    /// <summary>How long a completed duty waits for its commendations.</summary>
    public static readonly TimeSpan ArmedTimeout = TimeSpan.FromMinutes(15);

    private readonly Func<TimeSpan> _clock;
    private bool _tracking;
    private bool _eligible;
    private bool _completed;
    private bool _ignoreCurrentDuty;
    private bool _lastDutyStarted;
    private TimeSpan _startedAt;
    private TimeSpan _completedAt;
    private short _startingCommendations;
    private TimeSpan _observedDuration;

    /// <param name="clock">Monotonic elapsed time (for example a <c>Stopwatch</c>'s), never the wall clock.</param>
    public CommendationTracker(Func<TimeSpan> clock) => _clock = clock;

    /// <summary>Whether a duty is being observed (started, completed or not).</summary>
    public bool Tracking => _tracking;

    /// <summary>Whether an observed duty has completed and waits for its commendations.</summary>
    public bool Armed => _tracking && _completed;

    /// <summary>Rounds to the nearest five minutes (midpoints up), minimum five, then doubles.</summary>
    public static TimeSpan CalculateBonus(TimeSpan observedDuration)
    {
        var fiveMinuteUnits = Math.Round(
            observedDuration.TotalMinutes / 5d,
            MidpointRounding.AwayFromZero);
        var roundedMinutes = Math.Max(5d, fiveMinuteUnits * 5d);
        return TimeSpan.FromMinutes(roundedMinutes * 2d);
    }

    /// <summary>A duty started (the start event, or a duty already in progress when enabled): observe it from now.</summary>
    public void DutyStarted(short commendations, bool isDoll)
    {
        _ignoreCurrentDuty = false;
        _tracking = true;
        _eligible = isDoll;
        _completed = false;
        _startedAt = _clock();
        _completedAt = TimeSpan.Zero;
        _startingCommendations = commendations;
        _observedDuration = TimeSpan.Zero;
    }

    /// <summary>The observed duty completed successfully: arm it for <see cref="ArmedTimeout"/>.</summary>
    public void DutyCompleted(bool isDoll)
    {
        if (!_tracking || _completed)
            return;

        _eligible &= isDoll;
        _completedAt = _clock();
        _observedDuration = _completedAt - _startedAt;
        _completed = true;
    }

    /// <summary>
    /// Once per frame. Returns the award when the observed, completed duty got its first commendation increase;
    /// the caller adds the wind only when it is <see cref="CommendationAward.Eligible"/>.
    /// </summary>
    public CommendationAward? Update(bool loggedIn, bool dutyStarted, bool isDoll, short commendations)
    {
        var startedNow = dutyStarted && !_lastDutyStarted;
        _lastDutyStarted = dutyStarted;

        if (_ignoreCurrentDuty)
        {
            if (!dutyStarted)
                _ignoreCurrentDuty = false;
            return null;
        }

        // IDutyState intentionally does not raise DutyStarted after reconnect or for join-in-progress. Detect that
        // state and measure from the first observed frame.
        if (!_tracking)
        {
            if (loggedIn && dutyStarted)
                DutyStarted(commendations, isDoll);
            return null;
        }

        // A duty that ended without DutyCompleted was abandoned or failed.
        if (!_completed && !dutyStarted)
        {
            Reset();
            return null;
        }

        if (_completed)
        {
            // 06-A5: armed for at most 15 minutes.
            if (_clock() - _completedAt >= ArmedTimeout)
            {
                Reset();
                return null;
            }

            // 06-A5: another duty seen starting (join in progress raises no event): observe that one instead.
            if (startedNow && loggedIn)
            {
                DutyStarted(commendations, isDoll);
                return null;
            }
        }

        // Eligibility is intentionally sticky: leaving Doll at any point disqualifies this run.
        if (!isDoll)
            _eligible = false;

        if (!_completed || commendations <= _startingCommendations)
            return null;

        // Consume the first positive increase once, regardless of the number received.
        var award = new CommendationAward(
            CalculateBonus(_observedDuration),
            commendations - _startingCommendations,
            _observedDuration,
            _eligible && isDoll);
        Reset();
        _ignoreCurrentDuty = dutyStarted;
        return award;
    }

    /// <summary>Logout or disable: forget the duty.</summary>
    public void Reset()
    {
        _tracking = false;
        _eligible = false;
        _completed = false;
        _ignoreCurrentDuty = false;
        _startedAt = TimeSpan.Zero;
        _completedAt = TimeSpan.Zero;
        _startingCommendations = 0;
        _observedDuration = TimeSpan.Zero;
    }
}
