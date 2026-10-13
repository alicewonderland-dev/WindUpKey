#if WINDUP_TESTING || WINDUP_CALL_UNITS
using System;
using System.Collections.Generic;
using System.Numerics;

namespace WindUpKey.Services;

/// <summary>Why <see cref="CallStuckDetector"/> stopped a Call.</summary>
public enum CallStuckReason
{
    None,

    /// <summary>
    /// 07-D14: in <see cref="CallStuckDetector.NoProgressMs"/> she got no <see cref="CallStuckDetector.MinProgressYalms"/>
    /// closer to the next node than her best distance to it, and vnav reached no new waypoint.
    /// </summary>
    NoProgress,

    /// <summary>vnavmesh has had no path running, with no progress, for <see cref="CallStuckDetector.CannotPathMs"/> (and isn't pathfinding).</summary>
    CannotPath,
}

/// <summary>
/// 07-A4 (D6), made stricter by 07-D14: stops a Call whose pathing makes no headway, in Hardcore too. The usual stall is
/// pathing into a wall: the character jitters or slides sideways but gets no closer. Fed each tick of a pathing step
/// with:
/// <list type="bullet">
/// <item>the <b>final target</b>: the reset key (a move of more than <see cref="NewTargetYalms"/> starts over);</item>
/// <item>the <b>progress node</b>, vnav's current waypoint (the final target when the waypoint list isn't available),
/// and vnav's <b>waypoint count</b> (0 when unknown);</item>
/// <item>the <b>distance</b> to the progress node, computed by the caller with <see cref="ProgressDistance"/>, and whether
/// she is <b>airborne</b> (flying or diving), which picks 3D or XZ;</item>
/// <item>whether the <b>waypoints are available</b> (false with an older vnavmesh that has no waypoint IPC);</item>
/// <item>vnavmesh's state: <c>vnavmesh.Path.IsRunning</c> (a path is being followed) and
/// <c>vnavmesh.SimpleMove.PathfindInProgress</c> (a path is being computed).</item>
/// </list>
/// It is stuck (<see cref="CallStuckReason.NoProgress"/>) when, in <see cref="NoProgressMs"/> of active time, she neither
/// gets <see cref="MinProgressYalms"/> closer to the node than her best distance to it (as of the last progress, so a
/// step back never lowers the bar) nor has vnav advance to a new waypoint. A new node (more than
/// <see cref="NewTargetYalms"/> away from the last one) sets the best distance afresh without counting as progress, so a
/// re-path or a flip between the waypoint and the fallback can't fake headway. Taking off or landing (07-A15) does the
/// same, because the distance switches between XZ and 3D. The window is held while vnav is still computing a path
/// (pathfinding, no path running), and the no-progress check is off while the waypoints aren't available (07-A15:
/// measured to the final target, an ordinary detour would look stuck); the 3-minute travel step timeout still bounds
/// both.
/// vnavmesh's IPC has no "no path" answer: a pathfind that fails just ends with no path running, so "no path running,
/// with no progress, for <see cref="CannotPathMs"/>, and no pathfind still going" stands for it
/// (<see cref="CallStuckReason.CannotPath"/>). Time while paused (between areas, mounting, casting, at the door, or a
/// travel step that isn't pathing) doesn't count. Monotonic milliseconds from an injected clock. No Dalamud dependency.
/// </summary>
public sealed class CallStuckDetector(Func<long> clock)
{
    /// <summary>She must get at least this much closer to the node than her best distance ...</summary>
    public const float MinProgressYalms = 2f;

    /// <summary>... within this many active milliseconds (07-D14: 10 s), unless vnav reaches a new waypoint.</summary>
    public const long NoProgressMs = 10_000;

    /// <summary>No path running, with no progress, for this long.</summary>
    public const long CannotPathMs = 5_000;

    /// <summary>A target or node that moves further than this is a new one.</summary>
    public const float NewTargetYalms = 1f;

    private Vector3? _target;
    private Vector3 _node;
    private int _waypointCount;
    private bool _airborne;
    private long _lastMs;
    private long _activeMs;
    private long _windowMs;
    private float _bestDistance;
    private long _progressAtMs;
    private long? _idleSinceMs;

    /// <summary>
    /// 07-D14: the distance that counts as headway to <paramref name="node"/>: in 3D while flying or diving, else
    /// horizontal (XZ), so bobbing on stairs or a slope isn't progress.
    /// </summary>
    public static float ProgressDistance(Vector3 position, Vector3 node, bool airborne)
    {
        if (airborne)
            return Vector3.Distance(position, node);

        var dx = position.X - node.X;
        var dz = position.Z - node.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>Forget the target (a waiting phase, or a new Call).</summary>
    public void Reset() => _target = null;

    /// <summary>A travel tick that isn't pathing: the time until the next <see cref="Observe"/> doesn't count.</summary>
    public void Pause() => _lastMs = clock();

    /// <summary>
    /// One pathing tick. <paramref name="distance"/> is to <paramref name="progressNode"/> (<see cref="ProgressDistance"/>
    /// with <paramref name="airborne"/>); <paramref name="waypointCount"/> is vnav's (0 when unknown);
    /// <paramref name="waypointsAvailable"/> is false when vnav's waypoints can't be read (an older vnavmesh), which turns
    /// the no-progress check off; <paramref name="paused"/> holds the clock (between areas, mounting, casting, at the
    /// door). Returns why the Call is stuck, or <see cref="CallStuckReason.None"/>.
    /// </summary>
    public CallStuckReason Observe(
        Vector3 finalTarget,
        Vector3 progressNode,
        int waypointCount,
        bool waypointsAvailable,
        float distance,
        bool airborne,
        bool pathRunning,
        bool pathfinding,
        bool paused)
    {
        var now = clock();
        if (_target is not { } known || Vector3.Distance(known, finalTarget) > NewTargetYalms)
        {
            _target = finalTarget;
            _node = progressNode;
            _waypointCount = waypointCount;
            _airborne = airborne;
            _lastMs = now;
            _activeMs = 0;
            _windowMs = 0;
            _bestDistance = distance;
            _progressAtMs = 0;
            _idleSinceMs = null;
            return CallStuckReason.None;
        }

        var elapsed = Math.Max(0, now - _lastMs);
        _lastMs = now;
        if (paused)
            return CallStuckReason.None;

        _activeMs += elapsed;
        // 07-A11: a path still being computed holds the no-progress window (the can't-path count goes on, as before).
        // 07-A15: so do ticks without vnav's waypoints; with an older vnavmesh that is the whole Call.
        if (waypointsAvailable && !(pathfinding && !pathRunning))
            _windowMs += elapsed;

        // 07-D14: vnav dropped the waypoint it had reached and is heading for the next one.
        var advanced = pathRunning && waypointCount > 0 && waypointCount < _waypointCount;
        _waypointCount = waypointCount;
        if (Vector3.Distance(_node, progressNode) > NewTargetYalms)
        {
            // Another node (a new waypoint, a re-path, or the fallback to the final target): its best distance starts
            // here. This alone isn't progress, and it leaves the can't-path count alone (07-A11).
            _node = progressNode;
            _bestDistance = distance;
        }

        if (airborne != _airborne)
        {
            // 07-A15: taking off or landing switches the distance between XZ and 3D (a steep take-off adds the height at
            // once): the best distance starts here, as for a new node, without restarting the window.
            _airborne = airborne;
            _bestDistance = distance;
        }

        if (advanced || distance <= _bestDistance - MinProgressYalms)
        {
            _bestDistance = distance;
            _windowMs = 0;
            _progressAtMs = _activeMs;
            _idleSinceMs = null;
        }

        // A retry's short pathfind doesn't restart the count; a pathfind still running when it is due holds it.
        if (pathRunning)
            _idleSinceMs = null;
        else
            _idleSinceMs ??= _activeMs;

        if (_windowMs >= NoProgressMs)
            return CallStuckReason.NoProgress;
        if (!pathfinding
            && _idleSinceMs is { } idleSince
            && _activeMs - idleSince >= CannotPathMs
            && _activeMs - _progressAtMs >= CannotPathMs)
            return CallStuckReason.CannotPath;
        return CallStuckReason.None;
    }
}

/// <summary>
/// 07-A11, 07-A15: reads vnav's current waypoint, the node <see cref="CallStuckDetector"/> measures headway to. The count
/// is polled each tick; the list, which allocates, is read only when the count changes, and its first entry is cached.
/// Every call is guarded. An older vnavmesh (before 1.2.3.14) has no waypoint IPC: once a call says so (no IPC bound, or
/// an exception the caller calls "missing", <c>IpcNotReadyError</c>), it is remembered until the next Call, so it isn't
/// called, and doesn't throw, every tick, and the waypoints stay unavailable (the no-progress check is off). No Dalamud
/// dependency: the caller passes the IPC calls (null when not bound) and the "missing" test.
/// </summary>
public sealed class CallWaypointReader(
    Func<int?> readCount,
    Func<IReadOnlyList<Vector3>?> readList,
    Func<Exception, bool> isMissing)
{
    private int _count = -1;
    private Vector3? _first;

    /// <summary>The waypoint IPC is missing (an older vnavmesh); kept until <see cref="Reset"/> for a new Call.</summary>
    public bool Missing { get; private set; }

    /// <summary>Forget the cached waypoint (a waiting phase, a cancel). A new Call also forgets that the IPC was missing.</summary>
    public void Reset(bool newCall)
    {
        _count = -1;
        _first = null;
        if (newCall)
            Missing = false;
    }

    /// <summary>
    /// One pathing tick: the node to measure headway to (the first waypoint, else <paramref name="finalTarget"/>), vnav's
    /// waypoint count (0 when unknown), and whether the waypoints were read: false when the IPC is missing or failed, or
    /// the list couldn't be read for a path with waypoints, because the final target then stands in for the next node.
    /// </summary>
    public Vector3 Read(Vector3 finalTarget, out int waypointCount, out bool available)
    {
        waypointCount = 0;
        available = false;
        if (Missing)
            return finalTarget;

        try
        {
            if (readCount() is not { } count)
            {
                Missing = true;
                return finalTarget;
            }

            waypointCount = Math.Max(0, count);
        }
        catch (Exception ex)
        {
            // Missing for the rest of the Call, or a passing failure: the count is read again next tick.
            Missing = isMissing(ex);
            _count = -1;
            _first = null;
            return finalTarget;
        }

        if (waypointCount != _count)
        {
            _count = waypointCount;
            _first = null;
            if (waypointCount > 0)
            {
                try
                {
                    if (readList() is { Count: > 0 } waypoints)
                        _first = waypoints[0];
                }
                catch (Exception ex)
                {
                    // The final target, until the count changes again.
                    Missing = isMissing(ex);
                }
            }
        }

        available = !Missing && (waypointCount == 0 || _first is not null);
        return _first ?? finalTarget;
    }
}
#endif
