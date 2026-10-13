using System;

namespace WindUpKey.Services;

/// <summary>
/// 06-A2: the low-wind tracker's memory belongs to one character profile. The remaining time it saw last is for the
/// profile it was seen on; once <see cref="Configuration.ActiveContentId"/> is another one, that value is forgotten
/// silently, so switching from a wound character to an expired one prints no "Expired" line and plays no sound.
/// No Dalamud dependency.
/// </summary>
public sealed class LowWindProfileGuard
{
    /// <summary>The profile the last remaining time was seen on, or null before the first observation.</summary>
    private string? _trackedProfile;

    /// <summary>
    /// Returns <paramref name="lastRemaining"/> while <paramref name="activeProfile"/> is the tracked profile, otherwise
    /// starts tracking it and returns null (a first observation, which never warns or echoes).
    /// </summary>
    public TimeSpan? Filter(string? activeProfile, TimeSpan? lastRemaining)
    {
        var active = activeProfile ?? string.Empty;
        if (_trackedProfile is not null && string.Equals(_trackedProfile, active, StringComparison.Ordinal))
            return lastRemaining;

        _trackedProfile = active;
        return null;
    }
}
