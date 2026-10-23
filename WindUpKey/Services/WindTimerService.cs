using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using WindUpKey.Protocol;
using WindUpKey.Quest;

namespace WindUpKey.Services;

/// <summary>
/// Owns the doll's wind (dolls only): the three <see cref="WindLayers"/>, the broken state, and the lock they set.
/// <see cref="Configuration.ExpiryUtc"/> is derived from the layers (when the doll stops if nothing changes) for the
/// low-wind warnings, the relay and older builds. Never surface exact durations in doll UI/chat; partners get only a
/// percentage of normal wind (<see cref="PercentForWinder"/>).
/// </summary>
public sealed class WindTimerService
{
    private readonly Configuration _config;
    private readonly LockController _lock;
    private readonly GameCommandRunner _commands;
    private readonly IObjectTable _objects;
    private readonly ICondition _condition;
    private readonly LowWindWarningService _lowWind;
    private readonly IChatGui _chat;
    private bool _wasLocked;
    private bool _pendingLoginSit;
    private int _loginSitAttempts;
    private bool _relaySafetyBypass;
    private bool _relaySafetyBypassAnnounced;
    private bool _callTravelBypass;
    private bool _wasBroken;

    /// <summary>Raised after local ExpiryUtc changes (not when applying a relay sync).</summary>
    public Action? DollWindChanged { get; set; }

    public WindTimerService(
        Configuration config,
        LockController lockController,
        GameCommandRunner commands,
        IObjectTable objects,
        ICondition condition,
        LowWindWarningService lowWind,
        IChatGui chat)
    {
        _config = config;
        _lock = lockController;
        _commands = commands;
        _objects = objects;
        _condition = condition;
        _lowWind = lowWind;
        _chat = chat;
        // Only dolls start from timer state; winders are unlocked at role switch, not every tick.
        _wasLocked = config.IsDoll && IsTimerEmpty;
        _wasBroken = config.IsDoll && IsBroken;
        _lock.SetLocked(_wasLocked);
    }

    /// <summary>The doll's wind now (from the stored layers; an unmigrated profile reads its expiry).</summary>
    public WindLayers Layers
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            if (_config.WindAsOfUtc is not { } asOf)
                return WindLayers.FromExpiry(_config.ExpiryUtc, now, _config.MaxWindHours);
            // Max wind hours may have been raised since (an owner, or the doll): the drain since the last save is taken
            // first, under the layers as they were, and only then does player overwind fill the new room. Relayering
            // first would let the drain fall on quest overwind, which an unwind would then take as normal wind.
            var stored = new WindLayers(
                _config.NormalWindHours,
                _config.QuestOverwindHours,
                _config.PlayerOverwindHours,
                asOf,
                _config.BrokenUntilUtc);
            // Vacation: nothing drains, and a break's time left is kept, from the last save on.
            return (_config.IsOnVacation ? stored.Frozen(now) : stored.At(now)).Relayer(_config.MaxWindHours);
        }
    }

    /// <summary>
    /// A doll on vacation became a winder: vacation ends (winders never have it), with the wind kept exactly as it froze,
    /// so it runs on from here if they become a doll again. Works with the role already changed. The relay ends its own
    /// copy when they register as a winder.
    /// </summary>
    public void EndVacationForRoleChange()
    {
        if (_config.VacationSinceUtc is null)
            return;

        var now = DateTimeOffset.UtcNow;
        if (_config.WindAsOfUtc is { } asOf)
        {
            var frozen = new WindLayers(
                _config.NormalWindHours, _config.QuestOverwindHours, _config.PlayerOverwindHours, asOf, _config.BrokenUntilUtc)
                .Frozen(now);
            _config.VacationSinceUtc = null;
            WriteLayers(frozen, now);
            _config.WindUpdatedUtc = now;
        }

        _config.VacationSinceUtc = null;
        _config.VacationUpdatedUtc = now;
        _config.Save();
    }

    /// <summary>The doll is on vacation: frozen wind, free movement, and no winding, springs, Calls or quests.</summary>
    public bool IsOnVacation => _config.IsOnVacation;

    /// <summary>Raised after vacation mode turns on or off (the relay is told, and listeners refresh).</summary>
    public event Action? VacationChanged;

    /// <summary>
    /// Turns vacation mode on or off (doll only; the doll's own choice, at any time). The wind is stored as it is now, so
    /// on it freezes from here, and off it carries on from where it froze. <paramref name="updatedUtc"/> is the stamp for
    /// the relay's last-write-wins sync (another PC's change); null stamps it now. Returns false when nothing changed.
    /// </summary>
    public bool SetVacation(bool on, DateTimeOffset? sinceUtc = null, DateTimeOffset? updatedUtc = null)
    {
        if (!_config.IsDoll || on == _config.IsOnVacation)
            return false;

        var now = DateTimeOffset.UtcNow;
        if (updatedUtc is { } synced)
        {
            // Another PC's change: the wind isn't touched or stamped here. This PC's stored wind may be weeks stale;
            // the relay's row, applied right after by the wind sync, is the one the other PC froze or ran on. The lock
            // waits for it too (ApplySynced and the next tick sync it): synced now, the stale row could lock and sit them.
            // Ending: the frozen row is first moved on to now undrained (its stamp kept, so a newer relay row still wins
            // the wind sync). Without it, a relay that ended the vacation itself (a register as a winder) sends no newer
            // wind, and the whole vacation would drain at once from the frozen row's time.
            if (!on && _config.IsOnVacation && _config.WindAsOfUtc is { } asOf)
            {
                WriteLayers(new WindLayers(
                    _config.NormalWindHours, _config.QuestOverwindHours, _config.PlayerOverwindHours, asOf, _config.BrokenUntilUtc), now);
            }

            _config.VacationSinceUtc = on ? sinceUtc ?? now : null;
            _config.VacationUpdatedUtc = synced;
            _config.Save();
        }
        else
        {
            var current = Layers;
            _config.VacationSinceUtc = on ? now : null;
            _config.VacationUpdatedUtc = now;
            Save(current, now);
        }

        if (!on)
            _wasBroken = IsBroken;
        PluginChat.Print(
            _chat,
            on ? "You're on vacation. Your wind is frozen until you're back." : "Welcome back! Your wind runs again from where it froze.",
            PluginChat.Green);
        VacationChanged?.Invoke();
        return true;
    }

    /// <summary>Locked by wind: no normal wind, or broken. Meaningful for dolls only.</summary>
    public bool IsTimerEmpty => Layers.IsLockedAt(DateTimeOffset.UtcNow);

    /// <summary>Broken by careless winding: locked until repaired or the break ends.</summary>
    public bool IsBroken => _config.IsDoll && Layers.IsBrokenAt(DateTimeOffset.UtcNow);

    /// <summary>Has player overwind (more winding from others may break them).</summary>
    public bool IsOverwound => _config.IsDoll && Layers.IsOverwound;

    /// <summary>Normal wind as a percentage of max wind hours, 0–100: all a partner is ever told.</summary>
    public int PercentForWinder => _config.IsDoll ? Layers.PercentOfMax(DateTimeOffset.UtcNow, _config.MaxWindHours) : 0;

    /// <summary>What unwinding the doll costs now: normal wind and player overwind in 8h springs, rounded up.</summary>
    public int UnwindCost => SpringRules.UnwindCost(Layers.UnwindableHours(DateTimeOffset.UtcNow));

    /// <summary>
    /// Movement/teleport lock policy. False while the relay safety bypass or an active
    /// owner-call travel bypass is active even if the timer is empty — ExpiryUtc is left unchanged.
    /// </summary>
    public bool IsLocked => _config.IsDoll && !_config.IsOnVacation && IsTimerEmpty && !_relaySafetyBypass && !_callTravelBypass;

    /// <summary>
    /// Called from the composition root each framework tick before <see cref="Tick"/>.
    /// When true, suspends unwound movement/teleport blocking until the relay is reachable again.
    /// </summary>
    public void SetRelaySafetyBypass(bool bypass)
    {
        if (bypass == _relaySafetyBypass)
            return;

        _relaySafetyBypass = bypass;
        if (!bypass)
        {
            _relaySafetyBypassAnnounced = false;
            return;
        }

        // Only announce when this would have locked the doll (timer empty).
        if (!_relaySafetyBypassAnnounced && _config.IsDoll && IsTimerEmpty && !_callTravelBypass)
        {
            _relaySafetyBypassAnnounced = true;
            PluginChat.Print(
                _chat,
                "Host unreachable — movement unlocked until the relay reconnects.",
                PluginChat.Yellow);
        }
    }

    /// <summary>
    /// Temporarily suspends unwound movement/teleport locks while answering an owner call.
    /// Does not change <see cref="Configuration.ExpiryUtc"/>.
    /// </summary>
    public void SetCallTravelBypass(bool bypass)
    {
        if (bypass == _callTravelBypass)
            return;
        _callTravelBypass = bypass;
        if (!bypass)
            _lock.SetInputMute(false);
        // Unlock immediately — do not wait for the next framework Tick SyncLockState, or the
        // first Teleport UseAction is still RestrictionsActive and the cast never starts.
        SyncLockState();
    }

    /// <summary>07-A1: the Call bypass is on (the self-test's X-call-idle reads it).</summary>
    public bool IsCallTravelBypassActive => _callTravelBypass;

    /// <summary>Mute jump during Call auto-travel; walk/fly blocked unless automation is driving.</summary>
    public void SetCallTravelInputMute(bool mute, System.Numerics.Vector3? destination = null) =>
        _lock.SetInputMute(mute, destination);

    public void SetCallTravelMuteDestination(System.Numerics.Vector3 destination) =>
        _lock.SetInputMuteDestination(destination);

    /// <summary>During Call pathing, allow Jump for takeoff and apply vnav IgnoreUserInput tweaks.</summary>
    public void SetCallTravelPathingPassThrough(bool enabled) =>
        _lock.SetInputMuteAutomationPassThrough(enabled);

    /// <summary>
    /// While Lifestream (or zone transit) is moving the doll, allow walk/fly RMI under Call mute.
    /// </summary>
    public void SetCallTravelLifestreamDriving(bool driving) =>
        _lock.SetInputMuteLifestreamDriving(driving);

    /// <summary>
    /// During aetheryte cast: allow fly RMI only (mounted Teleport). Walk stays muted.
    /// </summary>
    public void SetCallTravelCastingFlyPass(bool allow) =>
        _lock.SetInputMuteCastingFlyPass(allow);

    /// <summary>
    /// Call once the logged-in character's profile is active (06-A2: from <see cref="RelayClient.ProfileActivated"/>,
    /// after each login or plugin load). Sits if currently unwound.
    /// </summary>
    public void OnLoggedIn()
    {
        if (!IsLocked || !_config.AutoGroundSit)
            return;

        _pendingLoginSit = true;
        _loginSitAttempts = 0;
    }

    /// <summary>A local wind that isn't another player's (the starter wind): normal wind only, up to max.</summary>
    public void AddHours(double hours)
    {
        if (!_config.IsDoll || _config.IsOnVacation || hours <= 0)
            return;

        var now = DateTimeOffset.UtcNow;
        var before = Layers;
        var after = before.AddNormal(hours, _config.MaxWindHours, now);
        Save(after, now);
        if (after.Normal > before.Normal)
            _lowWind.OnWoundReceived(TimeSpan.FromHours(after.Normal - before.Normal));
    }

    /// <summary>
    /// A wind from another player (doll only): normal wind up to max, then player overwind. Player overwind reaching max
    /// wind hours breaks the doll. A broken doll refuses it (<see cref="PlayerWindResult.Refused"/>), and the relay gives
    /// the springs back. <paramref name="winderIdentity"/> is optional Name@World (or name) of who wound them.
    /// </summary>
    public PlayerWindResult AddPlayerWind(double hours, string? winderIdentity = null)
    {
        if (!_config.IsDoll || _config.IsOnVacation)
            return new PlayerWindResult(true, 0, false, false, false);

        var now = DateTimeOffset.UtcNow;
        var before = Layers;
        if (before.IsBrokenAt(now))
            return new PlayerWindResult(true, PercentForWinder, false, true, false);

        var outcome = before.AddPlayer(hours, _config.MaxWindHours, now, _config.BrokenRecoveryMinutes);
        Save(outcome.Layers, now);
        var name = TryWinderDisplayName(winderIdentity);
        if (outcome.Broke)
        {
            _wasBroken = true;
            _lowWind.OnBroken(name);
        }
        else if (hours > 0)
        {
            // Sized to the wind, as quest wind is: the doll never learns where it landed.
            _lowWind.OnWoundReceived(TimeSpan.FromHours(hours), name);
        }

        return new PlayerWindResult(false, PercentForWinder, IsOverwound, outcome.Broke, outcome.Broke);
    }

    /// <summary>
    /// After max wind hours changed (the doll's own setting, or an owner's): the layers are stored as they now read, so a
    /// raised max moves player overwind into normal wind from this moment on.
    /// </summary>
    public void OnMaxWindHoursChanged()
    {
        if (!_config.IsDoll)
            return;

        var now = DateTimeOffset.UtcNow;
        Save(Layers, now);
    }

    /// <summary>A repair by another player: ends the break now. False when the doll wasn't broken.</summary>
    public bool Repair(string? winderIdentity = null)
    {
        if (!IsBroken || _config.IsOnVacation)
            return false;

        var now = DateTimeOffset.UtcNow;
        Save(Layers.Repair(now), now);
        _wasBroken = false;
        _lowWind.OnRepaired(TryWinderDisplayName(winderIdentity), stillUnwound: IsTimerEmpty);
        return true;
    }

    /// <summary>
    /// 06-B1: whether the safeword has paused remote control (owners' unwind, call and settings updates are refused).
    /// </summary>
    public bool IsRemoteControlPaused =>
        SafewordPause.IsActive(_config.SafewordPausedUntilUtc, DateTimeOffset.UtcNow, _config.HardcoreMode);

    /// <summary>06-B1: when the safeword's pause of remote control ends, or null when there is none.</summary>
    public DateTimeOffset? SafewordPauseEndsAt =>
        SafewordPause.EndsAt(_config.SafewordPausedUntilUtc, DateTimeOffset.UtcNow, _config.HardcoreMode);

    /// <summary>
    /// 06-B1: the safeword was used (the caller checked it, and cancels an active Call). Pauses remote control until
    /// <c>max(current, now + hours)</c>, and adds <paramref name="hours"/> of wind ignoring the owner's
    /// <see cref="Configuration.MaxWindHours"/> cap, though never shortening a longer timer (06-A8). Doll only.
    /// </summary>
    public void UseSafeword(double hours)
    {
        if (!_config.IsDoll)
            return;

        _config.SafewordPausedUntilUtc = SafewordPause.Extend(_config.SafewordPausedUntilUtc, DateTimeOffset.UtcNow, hours);
        _config.Save();

        // Vacation: the wind stays frozen as it was (the doll moves freely anyway); only remote control is paused.
        if (_config.IsOnVacation)
            return;

        // The safeword also ends a break at once. Then normal wind, then quest overwind with no cap: it never adds player
        // overwind, so it can't break them.
        var now = DateTimeOffset.UtcNow;
        if (IsBroken)
        {
            Save(Layers.Repair(now), now);
            _wasBroken = false;
        }

        Save(Layers.AddSafeword(hours, _config.MaxWindHours, now), now);
        if (hours > 0)
            _lowWind.OnWoundReceived(TimeSpan.FromHours(hours));
    }

    /// <summary>06-B1: the doll ends the pause early. Returns false when remote control wasn't paused.</summary>
    public bool ResumeRemoteControl()
    {
        var wasPaused = IsRemoteControlPaused;
        if (_config.SafewordPausedUntilUtc is not null)
        {
            _config.SafewordPausedUntilUtc = null;
            _config.Save();
        }

        return wasPaused;
    }

    /// <summary>
    /// A commendation bonus: normal wind only, up to max, with the dedicated vague commendation RP echo. Returns the time
    /// actually added.
    /// </summary>
    public TimeSpan AddCommendationWind(TimeSpan bonus, int commendationsReceived)
    {
        if (!_config.IsDoll || _config.IsOnVacation || bonus <= TimeSpan.Zero)
            return TimeSpan.Zero;

        var now = DateTimeOffset.UtcNow;
        var before = Layers;
        var after = before.AddNormal(bonus.TotalHours, _config.MaxWindHours, now);
        if (after.Normal <= before.Normal)
            return TimeSpan.Zero;

        Save(after, now);
        _lowWind.OnCommendationWind(commendationsReceived);
        return TimeSpan.FromHours(after.Normal - before.Normal);
    }

    /// <summary>
    /// Quest board wind: normal wind up to max, then quest overwind, so the two reach at most twice
    /// <see cref="Configuration.MaxWindHours"/> (<see cref="QuestRewardRules.OverwindCapHours"/>). Still emits the vague
    /// wound RP echo, sized to the reward.
    /// </summary>
    public void AddQuestWind(double hours)
    {
        if (!_config.IsDoll || _config.IsOnVacation || hours <= 0)
            return;

        var now = DateTimeOffset.UtcNow;
        Save(Layers.AddQuest(hours, _config.MaxWindHours, now), now);
        // The echo is sized to what the quest gave, not what fit under the cap: the board shows each reward's hours, so a
        // smaller echo (or none) would tell the doll the timer just reached the cap.
        _lowWind.OnWoundReceived(TimeSpan.FromHours(hours));
    }

    /// <summary>
    /// Stores <paramref name="layers"/> and the expiry derived from them, stamped for the relay's last-write-wins sync,
    /// then brings the lock, warnings and listeners up to date.
    /// </summary>
    private void Save(WindLayers layers, DateTimeOffset now)
    {
        WriteLayers(layers, now);
        _config.WindUpdatedUtc = now;
        _config.Save();
        SyncLockState();
        _lowWind.OnWindChanged();
        DollWindChanged?.Invoke();
    }

    private void WriteLayers(WindLayers layers, DateTimeOffset now)
    {
        // Vacation: stored as they are (a synced row from before is moved on undrained, its break's time left kept).
        var at = _config.IsOnVacation ? layers.Frozen(now) : layers.At(now);
        _config.NormalWindHours = at.Normal;
        _config.QuestOverwindHours = at.Quest;
        _config.PlayerOverwindHours = at.Player;
        _config.WindAsOfUtc = now;
        _config.BrokenUntilUtc = at.BrokenUntil;
        _config.ExpiryUtc = at.StopsAt(now);
    }

    private static string? TryWinderDisplayName(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
            return null;

        if (PlayerIdentity.TryParse(identity, out var name, out _))
            return name;

        var trimmed = identity.Trim();
        var at = trimmed.LastIndexOf('@');
        if (at > 0)
            return trimmed[..at].Trim();

        return trimmed.Length > 0 ? trimmed : null;
    }

    /// <summary>How long until the doll stops if nothing changes (0 while locked). Debug and warnings only.</summary>
    public TimeSpan RunningFor() => _config.IsDoll ? Layers.RunningFor(DateTimeOffset.UtcNow) : TimeSpan.Zero;

    public void Tick()
    {
        // Only dolls track lock from the timer. Do not touch lock state for winders each frame.
        if (!_config.IsDoll)
        {
            _pendingLoginSit = false;
            return;
        }

        TrackBreakEnd();
        SyncLockState();
        TryPendingLoginSit();
        TidySafewordPause();
    }

    /// <summary>
    /// A break that ended by itself: its time is stored as passed (so the wind runs on from here, and the relay and other
    /// PCs learn it), with the doll's RP line.
    /// </summary>
    private void TrackBreakEnd()
    {
        var broken = IsBroken;
        if (broken || !_wasBroken)
        {
            _wasBroken = broken;
            return;
        }

        // Only this character's own stored break ending says "recovered": after a character switch the flag may be the
        // previous one's, and this character has no break stored.
        _wasBroken = false;
        if (_config.BrokenUntilUtc is null)
            return;

        Save(Layers, DateTimeOffset.UtcNow);
        _lowWind.OnRecovered(stillUnwound: IsTimerEmpty);
    }

    /// <summary>
    /// 06-B1: a pause that has ended (or any pause in Hardcore) is cleared, and one more than 24 h ahead (a clock that
    /// jumped back while running) is clamped, so it still lifts. Saved only when it changes.
    /// </summary>
    private void TidySafewordPause()
    {
        var stored = _config.SafewordPausedUntilUtc;
        if (stored is null)
            return;

        var tidy = SafewordPause.EndsAt(stored, DateTimeOffset.UtcNow, _config.HardcoreMode);
        if (tidy == stored)
            return;

        _config.SafewordPausedUntilUtc = tidy;
        _config.Save();
    }

    private void TryPendingLoginSit()
    {
        if (!_pendingLoginSit)
            return;

        var locked = IsLocked;
        if (locked)
            _loginSitAttempts++;

        // 06-A2 / 06-A12 (LoginSitRule): already in a pose or emote loop (her own, or the lock emote played on a
        // switch): leave her in it.
        var canEmote = locked && CanEmoteNow();
        var alreadyPosed = canEmote && _lock.IsInPoseOrEmoteLoop();
        switch (LoginSitRule.Next(locked, canEmote, alreadyPosed, _loginSitAttempts))
        {
            case LoginSitStep.Done:
                _pendingLoginSit = false;
                return;
            case LoginSitStep.Wait:
                return;
        }

        if (LoginSitRule.FinishedAfterSit(_commands.TryExecuteLockEmote(), _loginSitAttempts))
            _pendingLoginSit = false;
    }

    private bool CanEmoteNow()
    {
        if (_objects.LocalPlayer is null)
            return false;

        return !_condition[ConditionFlag.BetweenAreas]
               && !_condition[ConditionFlag.BetweenAreas51];
    }

    /// <summary>
    /// An unwind (locks the doll): normal wind and player overwind go, quest overwind is kept (and held while they're at
    /// 0). Used by partner unwind permission. <paramref name="winderIdentity"/> is optional Name@World (or name) of who
    /// unwound them.
    /// </summary>
    public void ClearWind(string? winderIdentity = null)
    {
        if (!_config.IsDoll || _config.IsOnVacation)
            return;

        var now = DateTimeOffset.UtcNow;
        WriteLayers(Layers.Unwind(now), now);
        _config.WindUpdatedUtc = now;
        _config.Save();
        SyncLockState();
        _lowWind.OnCleared(TryWinderDisplayName(winderIdentity));
        DollWindChanged?.Invoke();
    }

    /// <summary>
    /// Applies the relay's stored wind without emitting <see cref="DollWindChanged"/>: its layers when it has them, else
    /// (a row an older build pushed) layers made from its expiry.
    /// </summary>
    public void ApplySynced(WindLayers? layers, DateTimeOffset? expiryUtc, DateTimeOffset updatedUtc)
    {
        if (!_config.IsDoll)
            return;

        var now = DateTimeOffset.UtcNow;
        WriteLayers(layers ?? WindLayers.FromExpiry(expiryUtc, now, _config.MaxWindHours), now);
        _config.WindUpdatedUtc = updatedUtc;
        _wasBroken = IsBroken;
        _config.Save();
        SyncLockState();
        _lowWind.OnWindChanged();
    }

    /// <summary>
    /// Call when leaving Doll (Winder or role picker): unlock movement once.
    /// Keeps ExpiryUtc so returning as Doll restores remaining wind time.
    /// </summary>
    public void SuspendDollRestrictions()
    {
        _wasLocked = false;
        _lock.SetLocked(false);
    }

    private void SyncLockState()
    {
        var locked = IsLocked;
        if (!locked)
            _pendingLoginSit = false;

        if (locked == _wasLocked)
            return;

        var becameLocked = locked && !_wasLocked;
        var becameUnlocked = !locked && _wasLocked;
        _wasLocked = locked;
        _lock.SetLocked(locked);

        if (becameLocked && _config.AutoGroundSit && _objects.LocalPlayer is not null)
        {
            // 06-A2: once it has played, a pending login sit would only toggle it off again.
            if (_commands.TryExecuteLockEmote())
                _pendingLoginSit = false;
        }
        else if (becameUnlocked && _objects.LocalPlayer is not null)
            _lock.RequestPoseCancel();
    }

    public static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
            return "0 minutes";

        var totalHours = (int)remaining.TotalHours;
        var minutes = remaining.Minutes;
        if (totalHours >= 24)
        {
            var days = totalHours / 24;
            var hours = totalHours % 24;
            return hours > 0 ? $"{days}d {hours}h" : $"{days}d";
        }

        if (totalHours > 0)
            return minutes > 0 ? $"{totalHours}h {minutes}m" : $"{totalHours}h";

        return $"{Math.Max(1, minutes)}m";
    }
}

/// <summary>What a player's wind did, as the doll tells the winder (never exact times).</summary>
/// <param name="Refused">Not applied: the doll is broken (or not a doll). The relay gives the springs back.</param>
/// <param name="Percent">Normal wind as a percentage of max (overwind reads 100).</param>
/// <param name="Overwound">Has player overwind.</param>
/// <param name="Broken">Is broken.</param>
/// <param name="BrokeNow">This wind broke them.</param>
public readonly record struct PlayerWindResult(bool Refused, int Percent, bool Overwound, bool Broken, bool BrokeNow);
