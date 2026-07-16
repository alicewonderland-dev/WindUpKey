using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using WindUpKey.Protocol;

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
    /// Movement/teleport lock policy. False while the relay safety bypass is active
    /// (host unreachable) even if the timer is empty — ExpiryUtc is left unchanged.
    /// </summary>
    public bool IsLocked => _config.IsDoll && IsTimerEmpty && !_relaySafetyBypass;

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
        if (!_relaySafetyBypassAnnounced && _config.IsDoll && IsTimerEmpty)
        {
            _relaySafetyBypassAnnounced = true;
            PluginChat.Print(
                _chat,
                "Host unreachable — movement unlocked until the relay reconnects.",
                PluginChat.Yellow);
        }
    }

    /// <summary>Call on login (or plugin load while already logged in). Sits if currently unwound.</summary>
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
    /// Adds a commendation bonus using the normal timer cap, but emits only the dedicated
    /// vague commendation RP echo. Returns the time actually added after applying the cap.
    /// </summary>
    public TimeSpan AddCommendationWind(TimeSpan bonus, int commendationsReceived)
    {
        var actualAdded = AddWind(bonus);
        if (actualAdded <= TimeSpan.Zero)
            return TimeSpan.Zero;

        _lowWind.OnCommendationWind(commendationsReceived);
        return actualAdded;
    }

    private TimeSpan AddWind(TimeSpan amount)
    {
        if (!_config.IsDoll || amount <= TimeSpan.Zero)
            return TimeSpan.Zero;

        var now = DateTimeOffset.UtcNow;
        var currentExpiry = _config.ExpiryUtc is { } exp && exp > now ? exp : now;
        var proposed = currentExpiry.Add(amount);
        var maxExpiry = now.AddHours(Math.Max(0.01, _config.MaxWindHours));
        if (proposed > maxExpiry)
            proposed = maxExpiry;

        var actualAdded = proposed - currentExpiry;
        _config.ExpiryUtc = proposed;
        _config.Save();
        SyncLockState();
        _lowWind.OnWindChanged();
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
    }

    private void TryPendingLoginSit()
    {
        if (!_pendingLoginSit)
            return;

        if (!IsLocked)
        {
            _pendingLoginSit = false;
            return;
        }

        _loginSitAttempts++;
        if (!CanEmoteNow())
        {
            if (_loginSitAttempts >= 1800)
                _pendingLoginSit = false;
            return;
        }

        if (_commands.TryExecuteLockEmote())
            _pendingLoginSit = false;
        else if (_loginSitAttempts >= 1800)
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
        _config.Save();
        SyncLockState();
        _lowWind.OnCleared(TryWinderDisplayName(winderIdentity));
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
            _commands.ExecuteLockEmote();
        else if (becameUnlocked && _objects.LocalPlayer is not null)
            _lock.RequestCancelPoseNudge();
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
