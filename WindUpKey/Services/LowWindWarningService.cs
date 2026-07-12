using System;
using Dalamud.Plugin.Services;

namespace WindUpKey.Services;

/// <summary>
/// Doll-only vague low-wind echo at random remaining times within
/// 20–28h / 6–12h / 45m–2h windows (+ login reminder by band, skipped within 30m of the
/// last echo unless remaining &lt; 1h).
/// Never prints exact remaining time; never warns as a result of adding wind.
/// Also echoes when winding expires or is cleared.
/// </summary>
public sealed class LowWindWarningService
{
    private const int FlagHigh = 1 << 0;
    private const int FlagMid = 1 << 1;
    private const int FlagLow = 1 << 2;

    private static readonly TimeSpan HighMin = TimeSpan.FromHours(20);
    private static readonly TimeSpan HighMax = TimeSpan.FromHours(28);
    private static readonly TimeSpan MidMin = TimeSpan.FromHours(6);
    private static readonly TimeSpan MidMax = TimeSpan.FromHours(12);
    private static readonly TimeSpan LowMin = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan LowMax = TimeSpan.FromHours(2);

    /// <summary>Login/enable reminder is skipped if a warning was sent more recently than this.</summary>
    private static readonly TimeSpan LoginResendCooldown = TimeSpan.FromMinutes(30);

    /// <summary>Always allow login/enable reminder when remaining is below this.</summary>
    private static readonly TimeSpan LoginAlwaysResendBelow = TimeSpan.FromHours(1);

    private readonly Configuration _config;
    private readonly IChatGui _chat;
    private readonly LowWindMessagesConfig _messages;
    private readonly Random _rng = new();
    private TimeSpan? _lastRemaining;

    public LowWindWarningService(Configuration config, IChatGui chat, LowWindMessagesConfig messages)
    {
        _config = config;
        _chat = chat;
        _messages = messages;
    }

    /// <summary>Call each framework tick. Fires only on a downward cross of a random trigger.</summary>
    public void Tick()
    {
        if (!TryGetRemaining(out var remaining))
        {
            // Natural expiry: had wind last tick, now empty.
            // Explicit ClearWind clears _lastRemaining first so this path does not double-echo.
            if (_lastRemaining is { } prev && prev > TimeSpan.Zero)
                PrintMessage(_messages.Expired);

            _lastRemaining = null;
            return;
        }

        EnsureAllTriggers();

        // First observation after empty/load, or time was added: re-arm silently, never print.
        if (_lastRemaining is not { } prevRemaining || remaining > prevRemaining)
        {
            ApplyWindExtension(remaining);
            _lastRemaining = remaining;
            return;
        }

        var fired = _config.LowWindWarningsFired;
        var changed = false;

        // Mild → urgent so a large downward jump still delivers each newly crossed tier once.
        if (TryFire(remaining, prevRemaining, FlagHigh, GetHighTrigger(), _messages.High, ref fired))
            changed = true;
        if (TryFire(remaining, prevRemaining, FlagMid, GetMidTrigger(), _messages.Mid, ref fired))
            changed = true;
        if (TryFire(remaining, prevRemaining, FlagLow, GetLowTrigger(), _messages.Low, ref fired))
            changed = true;

        if (changed)
        {
            _config.LowWindWarningsFired = fired;
            _config.Save();
        }

        _lastRemaining = remaining;
    }

    /// <summary>
    /// On login (or load while logged in): one message by band max (not the secret trigger);
    /// mark past tiers fired so Tick does not re-spam.
    /// Skips the chat line if a warning was sent within the last 30 minutes, unless remaining &lt; 1h.
    /// </summary>
    public void OnLoggedIn()
    {
        if (!TryGetRemaining(out var remaining) || remaining > HighMax)
            return;

        EnsureAllTriggers();

        string message;
        var fired = _config.LowWindWarningsFired;
        if (remaining <= LowMax)
        {
            message = _messages.Low;
            fired |= FlagHigh | FlagMid | FlagLow;
        }
        else if (remaining <= MidMax)
        {
            message = _messages.Mid;
            fired |= FlagHigh | FlagMid;
        }
        else
        {
            message = _messages.High;
            fired |= FlagHigh;
        }

        if (ShouldPrintLoginReminder(remaining))
            PrintMessage(message);

        if (fired != _config.LowWindWarningsFired)
        {
            _config.LowWindWarningsFired = fired;
            _config.Save();
        }

        _lastRemaining = remaining;
    }

    private bool ShouldPrintLoginReminder(TimeSpan remaining)
    {
        if (remaining < LoginAlwaysResendBelow)
            return true;

        if (_config.LowWindLastWarningUtc is not { } last)
            return true;

        return DateTimeOffset.UtcNow - last > LoginResendCooldown;
    }

    /// <summary>After AddHours: re-arm / mark past silently — never print.</summary>
    public void OnWindChanged()
    {
        if (!_config.IsDoll)
            return;

        if (!TryGetRemaining(out var remaining))
        {
            ClearAll();
            return;
        }

        ApplyWindExtension(remaining);
        _lastRemaining = remaining;
    }

    /// <summary>After ClearWind: echo that winding is spent, then reset flags and triggers.</summary>
    public void OnCleared()
    {
        // Clear tracking first so a re-entrant Tick (e.g. from chat/config save) cannot also echo.
        var shouldEcho = _config.IsDoll && _lastRemaining is { } prev && prev > TimeSpan.Zero;
        ClearAll();
        if (shouldEcho)
            PrintMessage(_messages.Expired);
    }

    /// <summary>Debug: print the alert message that would apply now, plus remaining time.</summary>
    public void PrintCheckStatus()
    {
        if (!_config.IsDoll)
        {
            _chat.Print("[Wind-Up Key] Check: not a doll — no low-wind alert.");
            return;
        }

        if (!TryGetRemaining(out var remaining))
        {
            _chat.Print("[Wind-Up Key] Check: timer empty — no low-wind alert.");
            return;
        }

        var remainingDisplay = WindTimerService.FormatRemaining(remaining);
        string message;
        if (remaining > HighMax)
            message = "(none yet)";
        else if (remaining <= LowMax)
            message = _messages.Low;
        else if (remaining <= MidMax)
            message = _messages.Mid;
        else
            message = _messages.High;

        _chat.Print($"[Wind-Up Key] Check: {message}");
        _chat.Print($"[Wind-Up Key] Check: remaining {remainingDisplay}.");
    }

    /// <summary>Debug: print trigger/fired/crossed state for each low-wind band.</summary>
    public void PrintDebugStatus()
    {
        if (!_config.IsDoll)
        {
            _chat.Print("[Wind-Up Key] Debug: not a doll — no low-wind triggers.");
            return;
        }

        if (!TryGetRemaining(out var remaining))
        {
            _chat.Print("[Wind-Up Key] Debug: timer empty — no low-wind triggers.");
            return;
        }

        EnsureAllTriggers();

        var fired = _config.LowWindWarningsFired;
        _chat.Print(
            $"[Wind-Up Key] Debug: triggers high={FormatTrigger(GetHighTrigger())} " +
            $"(fired={(fired & FlagHigh) != 0}, crossed={remaining <= GetHighTrigger()}), " +
            $"mid={FormatTrigger(GetMidTrigger())} " +
            $"(fired={(fired & FlagMid) != 0}, crossed={remaining <= GetMidTrigger()}), " +
            $"low={FormatTrigger(GetLowTrigger())} " +
            $"(fired={(fired & FlagLow) != 0}, crossed={remaining <= GetLowTrigger()}).");
    }

    private static string FormatTrigger(TimeSpan trigger) =>
        WindTimerService.FormatRemaining(trigger);

    private void ApplyWindExtension(TimeSpan remaining)
    {
        var fired = _config.LowWindWarningsFired;
        var triggersChanged = false;

        // Above band max: clear fired and re-roll so the tier can warn again later.
        if (remaining > HighMax)
        {
            fired &= ~FlagHigh;
            RollHigh();
            triggersChanged = true;
        }

        if (remaining > MidMax)
        {
            fired &= ~FlagMid;
            RollMid();
            triggersChanged = true;
        }

        if (remaining > LowMax)
        {
            fired &= ~FlagLow;
            RollLow();
            triggersChanged = true;
        }

        EnsureAllTriggers();

        // Still at or below the current trigger: mark fired silently (no downward cross after wind).
        if (remaining <= GetHighTrigger())
            fired |= FlagHigh;
        if (remaining <= GetMidTrigger())
            fired |= FlagMid;
        if (remaining <= GetLowTrigger())
            fired |= FlagLow;

        var firedChanged = fired != _config.LowWindWarningsFired;
        if (firedChanged)
            _config.LowWindWarningsFired = fired;

        if (firedChanged || triggersChanged)
            _config.Save();
    }

    /// <summary>Fire only when remaining crosses the trigger from above (prev &gt; trigger &gt;= remaining).</summary>
    private bool TryFire(
        TimeSpan remaining,
        TimeSpan previous,
        int flag,
        TimeSpan trigger,
        string message,
        ref int fired)
    {
        if ((fired & flag) != 0 || remaining > trigger)
            return false;

        // Already at/below trigger last tick, or appeared here via wind — do not print.
        if (previous <= trigger)
        {
            fired |= flag;
            return true; // persist silent mark, but no message
        }

        PrintMessage(message);
        fired |= flag;
        return true;
    }

    private bool TryGetRemaining(out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (!_config.IsDoll)
            return false;

        if (_config.ExpiryUtc is not { } exp)
            return false;

        remaining = exp - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero;
    }

    private void EnsureAllTriggers()
    {
        var changed = false;
        if (_config.LowWindTriggerHighSeconds <= 0)
        {
            RollHigh();
            changed = true;
        }

        if (_config.LowWindTriggerMidSeconds <= 0)
        {
            RollMid();
            changed = true;
        }

        if (_config.LowWindTriggerLowSeconds <= 0)
        {
            RollLow();
            changed = true;
        }

        if (changed)
            _config.Save();
    }

    private void RollHigh() =>
        _config.LowWindTriggerHighSeconds = RollSeconds(HighMin, HighMax);

    private void RollMid() =>
        _config.LowWindTriggerMidSeconds = RollSeconds(MidMin, MidMax);

    private void RollLow() =>
        _config.LowWindTriggerLowSeconds = RollSeconds(LowMin, LowMax);

    private double RollSeconds(TimeSpan min, TimeSpan max)
    {
        var minSec = min.TotalSeconds;
        var maxSec = max.TotalSeconds;
        return minSec + (_rng.NextDouble() * (maxSec - minSec));
    }

    private TimeSpan GetHighTrigger() => TimeSpan.FromSeconds(_config.LowWindTriggerHighSeconds);
    private TimeSpan GetMidTrigger() => TimeSpan.FromSeconds(_config.LowWindTriggerMidSeconds);
    private TimeSpan GetLowTrigger() => TimeSpan.FromSeconds(_config.LowWindTriggerLowSeconds);

    private void ClearAll()
    {
        _lastRemaining = null;
        var any = _config.LowWindWarningsFired != 0
                  || _config.LowWindTriggerHighSeconds != 0
                  || _config.LowWindTriggerMidSeconds != 0
                  || _config.LowWindTriggerLowSeconds != 0;
        if (!any)
            return;

        _config.LowWindWarningsFired = 0;
        _config.LowWindTriggerHighSeconds = 0;
        _config.LowWindTriggerMidSeconds = 0;
        _config.LowWindTriggerLowSeconds = 0;
        _config.Save();
    }

    private void PrintMessage(string body)
    {
        _chat.Print($"[Wind-Up Key] {body}");
        _config.LowWindLastWarningUtc = DateTimeOffset.UtcNow;
        _config.Save();
    }
}
