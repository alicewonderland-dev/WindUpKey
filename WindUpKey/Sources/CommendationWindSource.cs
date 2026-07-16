using System;
using System.Diagnostics;
using Dalamud.Game.DutyState;
using Dalamud.Plugin.Services;
using WindUpKey.Services;

namespace WindUpKey.Sources;

/// <summary>
/// Awards one local wind bonus when an eligible doll receives one or more commendations
/// after a successfully completed duty.
/// </summary>
public sealed class CommendationWindSource : IWindUpSource
{
    private readonly IDutyState _dutyState;
    private readonly IPlayerState _playerState;
    private readonly IFramework _framework;
    private readonly IClientState _clientState;
    private readonly Configuration _config;
    private readonly WindTimerService _timer;
    private readonly IPluginLog _log;

    private bool _enabled;
    private bool _tracking;
    private bool _eligible;
    private bool _completed;
    private bool _ignoreCurrentDuty;
    private long _startedTimestamp;
    private short _startingCommendations;
    private TimeSpan _observedDuration;

    public CommendationWindSource(
        IDutyState dutyState,
        IPlayerState playerState,
        IFramework framework,
        IClientState clientState,
        Configuration config,
        WindTimerService timer,
        IPluginLog log)
    {
        _dutyState = dutyState;
        _playerState = playerState;
        _framework = framework;
        _clientState = clientState;
        _config = config;
        _timer = timer;
        _log = log;
    }

    public void Enable()
    {
        if (_enabled)
            return;

        _dutyState.DutyStarted += OnDutyStarted;
        _dutyState.DutyCompleted += OnDutyCompleted;
        _framework.Update += OnFrameworkUpdate;
        _clientState.Logout += OnLogout;
        _enabled = true;

        // Join-in-progress, reconnect, or plugin load: measure only the observed portion.
        if (_dutyState.IsDutyStarted)
            BeginObservedDuty();
    }

    public void Dispose()
    {
        if (!_enabled)
            return;

        _dutyState.DutyStarted -= OnDutyStarted;
        _dutyState.DutyCompleted -= OnDutyCompleted;
        _framework.Update -= OnFrameworkUpdate;
        _clientState.Logout -= OnLogout;
        _enabled = false;
        Reset();
    }

    /// <summary>Rounds to the nearest five minutes (midpoints up), minimum five, then doubles.</summary>
    public static TimeSpan CalculateBonus(TimeSpan observedDuration)
    {
        var fiveMinuteUnits = Math.Round(
            observedDuration.TotalMinutes / 5d,
            MidpointRounding.AwayFromZero);
        var roundedMinutes = Math.Max(5d, fiveMinuteUnits * 5d);
        return TimeSpan.FromMinutes(roundedMinutes * 2d);
    }

    private void OnDutyStarted(IDutyStateEventArgs args) => BeginObservedDuty();

    private void BeginObservedDuty()
    {
        _ignoreCurrentDuty = false;
        _tracking = true;
        _eligible = _config.IsDoll;
        _completed = false;
        _startedTimestamp = Stopwatch.GetTimestamp();
        _startingCommendations = _playerState.PlayerCommendations;
        _observedDuration = TimeSpan.Zero;
    }

    private void OnDutyCompleted(IDutyStateEventArgs args)
    {
        if (!_tracking || _completed)
            return;

        _eligible &= _config.IsDoll;
        _observedDuration = Stopwatch.GetElapsedTime(_startedTimestamp);
        _completed = true;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (_ignoreCurrentDuty)
        {
            if (!_dutyState.IsDutyStarted)
                _ignoreCurrentDuty = false;
            return;
        }

        // IDutyState intentionally does not raise DutyStarted after reconnect or for
        // join-in-progress. Detect that state and measure from the first observed frame.
        if (!_tracking)
        {
            if (_clientState.IsLoggedIn && _dutyState.IsDutyStarted)
                BeginObservedDuty();
            return;
        }

        // A duty that ended without DutyCompleted was abandoned or failed.
        if (!_completed && !_dutyState.IsDutyStarted)
        {
            Reset();
            return;
        }

        // Eligibility is intentionally sticky: leaving Doll at any point disqualifies this run.
        if (!_config.IsDoll)
            _eligible = false;

        var currentCommendations = _playerState.PlayerCommendations;
        if (!_completed || currentCommendations <= _startingCommendations)
            return;

        // Consume the first positive increase once, regardless of the number received.
        var commendationsReceived = currentCommendations - _startingCommendations;
        var eligible = _eligible && _config.IsDoll;
        var observedDuration = _observedDuration;
        var bonus = CalculateBonus(observedDuration);
        Reset();
        _ignoreCurrentDuty = _dutyState.IsDutyStarted;

        if (!eligible)
            return;

        var actualAdded = _timer.AddCommendationWind(bonus, commendationsReceived);
        if (actualAdded > TimeSpan.Zero)
            _log.Information(
                "Commendation wind awarded from observed duty duration {ObservedDuration}",
                observedDuration);
    }

    private void OnLogout(int type, int code) => Reset();

    private void Reset()
    {
        _tracking = false;
        _eligible = false;
        _completed = false;
        _ignoreCurrentDuty = false;
        _startedTimestamp = 0;
        _startingCommendations = 0;
        _observedDuration = TimeSpan.Zero;
    }
}
