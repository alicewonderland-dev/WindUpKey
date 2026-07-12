using System;
using Dalamud.Plugin.Services;

namespace WindUpKey.Services;

/// <summary>
/// Doll-only vague low-wind echo at 24h / 8h / 1h remaining (and on login when already low).
/// Never prints exact remaining time.
/// </summary>
public sealed class LowWindWarningService
{
    private const int Flag24h = 1 << 0;
    private const int Flag8h = 1 << 1;
    private const int Flag1h = 1 << 2;

    private static readonly TimeSpan Threshold24h = TimeSpan.FromHours(24);
    private static readonly TimeSpan Threshold8h = TimeSpan.FromHours(8);
    private static readonly TimeSpan Threshold1h = TimeSpan.FromHours(1);

    private readonly Configuration _config;
    private readonly IChatGui _chat;

    public LowWindWarningService(Configuration config, IChatGui chat)
    {
        _config = config;
        _chat = chat;
    }

    /// <summary>Call each framework tick. Fires any newly crossed thresholds once each.</summary>
    public void Tick()
    {
        if (!TryGetRemaining(out var remaining))
            return;

        var fired = _config.LowWindWarningsFired;
        var changed = false;

        // Mild → urgent so a large jump (e.g. AFK) still delivers each tier once.
        if (remaining <= Threshold24h && (fired & Flag24h) == 0)
        {
            PrintMessage(Message24h);
            fired |= Flag24h;
            changed = true;
        }

        if (remaining <= Threshold8h && (fired & Flag8h) == 0)
        {
            PrintMessage(Message8h);
            fired |= Flag8h;
            changed = true;
        }

        if (remaining <= Threshold1h && (fired & Flag1h) == 0)
        {
            PrintMessage(Message1h);
            fired |= Flag1h;
            changed = true;
        }

        if (changed)
        {
            _config.LowWindWarningsFired = fired;
            _config.Save();
        }
    }

    /// <summary>
    /// On login (or load while logged in): one message for the most urgent crossed tier;
    /// mark all crossed tiers fired so Tick does not re-spam.
    /// </summary>
    public void OnLoggedIn()
    {
        if (!TryGetRemaining(out var remaining) || remaining > Threshold24h)
            return;

        var fired = _config.LowWindWarningsFired;
        string message;
        if (remaining <= Threshold1h)
        {
            message = Message1h;
            fired |= Flag24h | Flag8h | Flag1h;
        }
        else if (remaining <= Threshold8h)
        {
            message = Message8h;
            fired |= Flag24h | Flag8h;
        }
        else
        {
            message = Message24h;
            fired |= Flag24h;
        }

        PrintMessage(message);
        if (fired == _config.LowWindWarningsFired)
            return;

        _config.LowWindWarningsFired = fired;
        _config.Save();
    }

    /// <summary>After AddHours: clear warning bits for thresholds now strictly above remaining.</summary>
    public void OnWindChanged()
    {
        if (!_config.IsDoll)
            return;

        if (!TryGetRemaining(out var remaining))
        {
            ClearAllFlags();
            return;
        }

        ClearFlagsAbove(remaining);
    }

    /// <summary>After ClearWind: reset all low-wind warning flags.</summary>
    public void OnCleared() => ClearAllFlags();

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

    private void ClearFlagsAbove(TimeSpan remaining)
    {
        var fired = _config.LowWindWarningsFired;
        var next = fired;

        if (remaining > Threshold24h)
            next &= ~Flag24h;
        if (remaining > Threshold8h)
            next &= ~Flag8h;
        if (remaining > Threshold1h)
            next &= ~Flag1h;

        if (next == fired)
            return;

        _config.LowWindWarningsFired = next;
        _config.Save();
    }

    private void ClearAllFlags()
    {
        if (_config.LowWindWarningsFired == 0)
            return;

        _config.LowWindWarningsFired = 0;
        _config.Save();
    }

    private void PrintMessage(string body) =>
        _chat.Print($"[Wind-Up Key] {body}");

    private const string Message24h =
        "Your springs feel a little less taut — your winding is beginning to ebb.";

    private const string Message8h =
        "Your key is running low. Seek winding soon, before your steps grow stiff.";

    private const string Message1h =
        "Your winding is nearly spent. Find a winder before you seize up.";
}
