using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using WindUpKey.Protocol;
using WindUpKey.Quest;

namespace WindUpKey.Services;

/// <summary>
/// Owns expiry state for dolls only. Exact remaining duration is returned for the person winding —
/// never surface it in doll UI/chat (vague low-wind echo is handled by <see cref="LowWindWarningService"/>).
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
        _lock.SetLocked(_wasLocked);
    }

    /// <summary>Empty/expired timer. Meaningful for dolls only.</summary>
    public bool IsTimerEmpty =>
        _config.ExpiryUtc is null || _config.ExpiryUtc.Value <= DateTimeOffset.UtcNow;

    /// <summary>
    /// Movement/teleport lock policy. False while the relay safety bypass or an active
    /// owner-call travel bypass is active even if the timer is empty — ExpiryUtc is left unchanged.
    /// </summary>
    public bool IsLocked => _config.IsDoll && IsTimerEmpty && !_relaySafetyBypass && !_callTravelBypass;

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

    /// <summary>
    /// Extend the timer by hours (doll only). Returns remaining span for the remote winder.
    /// <paramref name="winderIdentity"/> is optional Name@World (or name) of who wound them.
    /// </summary>
    public TimeSpan AddHours(double hours, string? winderIdentity = null)
    {
        if (!_config.IsDoll)
            return TimeSpan.Zero;

        if (hours <= 0)
            return RemainingForWinder();

        var actualAdded = AddWind(TimeSpan.FromHours(hours));
        if (actualAdded > TimeSpan.Zero)
            _lowWind.OnWoundReceived(actualAdded, TryWinderDisplayName(winderIdentity));
        return RemainingForWinder();
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

        var actualAdded = AddWind(TimeSpan.FromHours(hours), ignoreMaxCap: true);
        if (actualAdded > TimeSpan.Zero)
            _lowWind.OnWoundReceived(actualAdded);
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
    /// Adds a commendation bonus using the normal timer cap (never shortening the timer, 06-A8), but emits only the
    /// dedicated vague commendation RP echo. Returns the time actually added after applying the cap.
    /// </summary>
    public TimeSpan AddCommendationWind(TimeSpan bonus, int commendationsReceived)
    {
        var actualAdded = AddWind(bonus);
        if (actualAdded <= TimeSpan.Zero)
            return TimeSpan.Zero;

        _lowWind.OnCommendationWind(commendationsReceived);
        return actualAdded;
    }

    /// <summary>
    /// Quest board path: extends the timer up to the overwind cap (<see cref="QuestRewardRules.OverwindCapHours"/>, twice
    /// <see cref="Configuration.MaxWindHours"/>), never shortening a longer timer (06-A8). Still emits the vague wound RP
    /// echo. Returns the time actually added.
    /// </summary>
    public TimeSpan AddQuestWind(double hours)
    {
        if (!_config.IsDoll || hours <= 0)
            return TimeSpan.Zero;

        var actualAdded = AddWind(TimeSpan.FromHours(hours), capHours: QuestRewardRules.OverwindCapHours(_config.MaxWindHours));
        // The echo is sized to what the quest gave, not what fit under the cap: the board shows each reward's hours, so a
        // smaller echo (or none) would tell the doll the timer just reached the cap.
        _lowWind.OnWoundReceived(TimeSpan.FromHours(hours));
        return actualAdded;
    }

    /// <param name="ignoreMaxCap">No cap at all (the safeword).</param>
    /// <param name="capHours">A cap other than <see cref="Configuration.MaxWindHours"/> (quest overwind).</param>
    private TimeSpan AddWind(TimeSpan amount, bool ignoreMaxCap = false, double? capHours = null)
    {
        if (!_config.IsDoll || amount <= TimeSpan.Zero)
            return TimeSpan.Zero;

        // 06-A8: capped at MaxWindHours (or capHours) from now, but never shorter than the current expiry.
        var (proposed, actualAdded) = WindMath.Add(
            DateTimeOffset.UtcNow,
            _config.ExpiryUtc,
            amount,
            ignoreMaxCap ? null : capHours ?? _config.MaxWindHours);
        _config.ExpiryUtc = proposed;
        _config.WindUpdatedUtc = DateTimeOffset.UtcNow;
        _config.Save();
        SyncLockState();
        _lowWind.OnWindChanged();
        DollWindChanged?.Invoke();
        return actualAdded;
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

    public TimeSpan RemainingForWinder()
    {
        if (!_config.IsDoll)
            return TimeSpan.Zero;

        if (_config.ExpiryUtc is not { } exp)
            return TimeSpan.Zero;
        var remaining = exp - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    public void Tick()
    {
        // Only dolls track lock from the timer. Do not touch lock state for winders each frame.
        if (!_config.IsDoll)
        {
            _pendingLoginSit = false;
            return;
        }

        SyncLockState();
        TryPendingLoginSit();
        TidySafewordPause();
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
    /// Clear remaining wind (locks the doll). Used by partner unwind permission.
    /// <paramref name="winderIdentity"/> is optional Name@World (or name) of who unwound them.
    /// </summary>
    public void ClearWind(string? winderIdentity = null)
    {
        if (!_config.IsDoll)
            return;

        _config.ExpiryUtc = null;
        _config.WindUpdatedUtc = DateTimeOffset.UtcNow;
        _config.Save();
        SyncLockState();
        _lowWind.OnCleared(TryWinderDisplayName(winderIdentity));
        DollWindChanged?.Invoke();
    }

    /// <summary>
    /// Applies relay-authoritative wind expiry without emitting <see cref="DollWindChanged"/>.
    /// </summary>
    public void ApplySyncedExpiry(DateTimeOffset? expiryUtc, DateTimeOffset updatedUtc)
    {
        if (!_config.IsDoll)
            return;

        _config.ExpiryUtc = expiryUtc;
        _config.WindUpdatedUtc = updatedUtc;
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
