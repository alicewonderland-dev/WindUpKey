using System;

namespace WindUpKey.Services;

/// <summary>
/// Owns expiry state for dolls only. Remaining duration is returned for the person winding —
/// never surface it in doll UI/chat.
/// </summary>
public sealed class WindTimerService
{
    private readonly Configuration _config;
    private readonly LockController _lock;
    private bool _wasLocked;

    public WindTimerService(Configuration config, LockController lockController)
    {
        _config = config;
        _lock = lockController;
        // Only dolls start from timer state; winders are unlocked at role switch, not every tick.
        _wasLocked = config.IsDoll && IsTimerEmpty;
        _lock.SetLocked(_wasLocked);
    }

    /// <summary>Empty/expired timer. Meaningful for dolls only.</summary>
    public bool IsTimerEmpty =>
        _config.ExpiryUtc is null || _config.ExpiryUtc.Value <= DateTimeOffset.UtcNow;

    public bool IsLocked => _config.IsDoll && IsTimerEmpty;

    /// <summary>Extend the timer by hours (doll only). Returns remaining span for the remote winder.</summary>
    public TimeSpan AddHours(double hours)
    {
        if (!_config.IsDoll)
            return TimeSpan.Zero;

        if (hours <= 0)
            return RemainingForWinder();

        var now = DateTimeOffset.UtcNow;
        var currentExpiry = _config.ExpiryUtc is { } exp && exp > now ? exp : now;
        var proposed = currentExpiry.AddHours(hours);
        var maxExpiry = now.AddHours(Math.Max(0.01, _config.MaxWindHours));
        if (proposed > maxExpiry)
            proposed = maxExpiry;

        _config.ExpiryUtc = proposed;
        _config.Save();
        SyncLockState();
        return RemainingForWinder();
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
            return;

        SyncLockState();
    }

    /// <summary>Call when switching to Winder (or Unset): drop doll timer and unlock movement once.</summary>
    public void ClearDollRestrictions()
    {
        if (_config.ExpiryUtc is not null)
        {
            _config.ExpiryUtc = null;
            _config.Save();
        }

        _wasLocked = false;
        _lock.SetLocked(false);
    }

    private void SyncLockState()
    {
        var locked = IsLocked;
        if (locked == _wasLocked)
            return;

        _wasLocked = locked;
        _lock.SetLocked(locked);
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
