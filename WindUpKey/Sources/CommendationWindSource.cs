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

    private static readonly long ClockOrigin = Stopwatch.GetTimestamp();

    /// <summary>06-A5: the duty and commendation rules, Dalamud-free, on a monotonic clock.</summary>
    private readonly CommendationTracker _tracker = new(() => Stopwatch.GetElapsedTime(ClockOrigin));

    private bool _enabled;

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
            _tracker.DutyStarted(_playerState.PlayerCommendations, _config.IsDoll);
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
        _tracker.Reset();
    }

    private void OnDutyStarted(IDutyStateEventArgs args) =>
        _tracker.DutyStarted(_playerState.PlayerCommendations, _config.IsDoll);

    private void OnDutyCompleted(IDutyStateEventArgs args) => _tracker.DutyCompleted(_config.IsDoll);

    private void OnFrameworkUpdate(IFramework framework)
    {
        var award = _tracker.Update(
            _clientState.IsLoggedIn,
            _dutyState.IsDutyStarted,
            _config.IsDoll,
            _playerState.PlayerCommendations);
        if (award is not { Eligible: true } earned)
            return;

        var actualAdded = _timer.AddCommendationWind(earned.Bonus, earned.CommendationsReceived);
        if (actualAdded > TimeSpan.Zero)
            _log.Information(
                "Commendation wind awarded from observed duty duration {ObservedDuration}",
                earned.ObservedDuration);
    }

    private void OnLogout(int type, int code) => _tracker.Reset();
}
