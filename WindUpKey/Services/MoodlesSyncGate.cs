using System;

namespace WindUpKey.Services;

/// <summary>Coarse wind charge shown as a Moodle (see <see cref="MoodlesWindStatusService"/>).</summary>
public enum WindChargeLevel
{
    FullyWound,
    Wound,
    Low,
    NearlySpent,
    Unwound,
}

/// <summary>What the Moodles wind status does on this tick (<see cref="MoodlesSyncGate.Next"/>).</summary>
public enum MoodlesStep
{
    /// <summary>The status isn't wanted (off, not a doll, or not logged in): clear ours, never touch Moodles' IPC.</summary>
    Off,

    /// <summary>Moodles isn't known to be available, and a probe is due: probe it now.</summary>
    Probe,

    /// <summary>Moodles isn't available and was probed less than <see cref="MoodlesSyncGate.ProbeInterval"/> ago.</summary>
    Wait,

    /// <summary>Moodles is available.</summary>
    Ready,
}

/// <summary>
/// 06-A3: when the Moodles wind status may probe Moodles' IPC and retry applying its status. No Dalamud dependency;
/// the caller passes the clock (UTC now).
/// <list type="bullet">
/// <item>Nothing is probed unless the status is wanted (<see cref="Configuration.MoodlesStatusEnabled"/>, a doll,
/// logged in), so a winder, a doll who turned it off, or anyone without Moodles calls into it only for the one clear
/// per login (or turn-off) below.</item>
/// <item>While Moodles is unavailable, it is probed at most every <see cref="ProbeInterval"/>.</item>
/// <item>After an apply (or reflection rebind) that failed, the next attempt waits <see cref="FirstBackoff"/>, doubling
/// up to <see cref="MaxBackoff"/>. Moodles' Ready, a login and a change of level start again at once.</item>
/// <item>06-A13: the status stopping being wanted (turned off, the role changed), a login, or Moodles' Ready asks for
/// one probe and clear on the next tick while it isn't wanted (<see cref="RequestClear"/>, <see cref="TakeClearRequest"/>),
/// so a leftover permanent wind moodle, which Moodles keeps across sessions and others see, doesn't stay.</item>
/// </list>
/// Framework thread only (not thread-safe), except <see cref="RequestClear"/>.
/// </summary>
public sealed class MoodlesSyncGate
{
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private DateTime? _lastProbeUtc;
    private TimeSpan _backoff;
    private DateTime? _nextApplyUtc;
    private WindChargeLevel? _lastLevel;

    /// <summary>06-A13: set by <see cref="RequestClear"/> (the UI), taken by <see cref="TakeClearRequest"/> (the tick).</summary>
    private volatile bool _clearRequested;

    /// <summary>The wait after the last failure (zero when the last attempt succeeded or the backoff was reset).</summary>
    public TimeSpan CurrentBackoff => _backoff;

    /// <summary>
    /// The step for this tick. Checks the settings first: <see cref="MoodlesStep.Probe"/> only when the status is
    /// wanted, Moodles isn't available, and the last probe was at least <see cref="ProbeInterval"/> ago (a probe is
    /// counted from here).
    /// </summary>
    public MoodlesStep Next(bool statusEnabled, bool isDoll, bool loggedIn, bool moodlesAvailable, DateTime utcNow)
    {
        if (!statusEnabled || !isDoll || !loggedIn)
            return MoodlesStep.Off;

        if (moodlesAvailable)
            return MoodlesStep.Ready;

        return TryStartProbe(utcNow) ? MoodlesStep.Probe : MoodlesStep.Wait;
    }

    /// <summary>
    /// Whether a probe may run now (at most one per <see cref="ProbeInterval"/>), counting it when it may. For Moodles
    /// features with their own setting (the automation re-apply), which the caller has already checked.
    /// </summary>
    public bool TryStartProbe(DateTime utcNow)
    {
        if (_lastProbeUtc is { } last && utcNow - last < ProbeInterval)
            return false;

        _lastProbeUtc = utcNow;
        return true;
    }

    /// <summary>
    /// Whether applying <paramref name="level"/> (with its reflection rebind) may be tried now. A level other than the
    /// last one asked for starts the backoff again.
    /// </summary>
    public bool MayApply(WindChargeLevel level, DateTime utcNow)
    {
        if (_lastLevel != level)
        {
            _lastLevel = level;
            ResetBackoff();
        }

        return _nextApplyUtc is not { } next || utcNow >= next;
    }

    /// <summary>The apply or rebind failed: wait <see cref="FirstBackoff"/>, then twice as long each time, up to <see cref="MaxBackoff"/>.</summary>
    public void ApplyFailed(DateTime utcNow)
    {
        _backoff = _backoff <= TimeSpan.Zero
            ? FirstBackoff
            : TimeSpan.FromTicks(Math.Min(_backoff.Ticks * 2, MaxBackoff.Ticks));
        _nextApplyUtc = utcNow + _backoff;
    }

    /// <summary>The apply succeeded: no wait before the next one.</summary>
    public void ApplySucceeded() => ResetBackoff();

    /// <summary>A login: try again at once.</summary>
    public void ResetBackoff()
    {
        _backoff = TimeSpan.Zero;
        _nextApplyUtc = null;
    }

    /// <summary>
    /// 06-A13: the status stopped being wanted (turned off, the role changed) or may have been left over (a login,
    /// Moodles' Ready). Only sets a flag (any thread).
    /// </summary>
    public void RequestClear() => _clearRequested = true;

    /// <summary>
    /// 06-A13: whether this tick does the one probe and clear asked for by <see cref="RequestClear"/>. Each request is
    /// taken once, and acts only if the status isn't wanted (off, or not a doll): wanted, the normal sync takes over. A
    /// permanent wind moodle outlives the session and the role, so a winder clears a leftover one too. Logged out, the
    /// request waits for the login instead of being dropped. Nothing else probes while the status stays unwanted.
    /// </summary>
    public bool TakeClearRequest(bool statusEnabled, bool isDoll, bool loggedIn)
    {
        if (!_clearRequested || !loggedIn)
            return false;

        _clearRequested = false;
        return !(statusEnabled && isDoll);
    }

    /// <summary>Moodles announced it is ready: probe and try again at once.</summary>
    public void OnMoodlesReady()
    {
        _lastProbeUtc = null;
        ResetBackoff();
    }
}
