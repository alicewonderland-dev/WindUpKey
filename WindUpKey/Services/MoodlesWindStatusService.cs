using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;

namespace WindUpKey.Services;

/// <summary>
/// Applies a no-timer Moodles status on the local doll that reflects coarse wind charge.
/// Exact remaining time is never shown (NoExpire only). Sync plugins (e.g. Lightless)
/// are assumed to replicate local Moodles state to partners.
/// <para>
/// Moodles IPC ApplyMoodlesByString / ByData hit <c>CheckWhitelistGlobal</c>; with default
/// BroadcastAllow* settings that silently rejects self-apply. We therefore call Moodles'
/// local <c>MyStatusManager.AddOrUpdate</c> via reflection instead.
/// </para>
/// </summary>
public sealed class MoodlesWindStatusService : IDisposable
{
    private const int MinMoodlesApiVersion = 4;

    // Stable GUIDs so AddOrUpdate/Remove stay consistent across sessions and sync.
    private static readonly Guid FullyWoundGuid = Guid.Parse("a1b2c3d4-e5f6-4701-8901-0000c1a66e01");
    private static readonly Guid WoundGuid = Guid.Parse("a1b2c3d4-e5f6-4701-8902-0000c1a66e02");
    private static readonly Guid LowGuid = Guid.Parse("a1b2c3d4-e5f6-4701-8903-0000c1a66e03");
    private static readonly Guid NearlySpentGuid = Guid.Parse("a1b2c3d4-e5f6-4701-8904-0000c1a66e04");
    private static readonly Guid UnwoundGuid = Guid.Parse("a1b2c3d4-e5f6-4701-8905-0000c1a66e05");

    private static readonly TimeSpan FullyWoundMin = TimeSpan.FromHours(28);
    private static readonly TimeSpan WoundMin = TimeSpan.FromHours(12);
    private static readonly TimeSpan LowMin = TimeSpan.FromHours(2);

    private static readonly IReadOnlyList<Guid> AllLevelGuids =
    [
        FullyWoundGuid,
        WoundGuid,
        LowGuid,
        NearlySpentGuid,
        UnwoundGuid,
    ];

    private readonly IClientState _clientState;
    private readonly IObjectTable _objects;
    private readonly Configuration _config;
    private readonly WindTimerService _timer;
    private readonly LowWindMessagesConfig _messages;
    private readonly IPluginLog _log;

    private readonly ICallGateSubscriber<object> _ready;
    private readonly ICallGateSubscriber<object> _unloading;
    private readonly ICallGateSubscriber<int> _version;

    private bool _moodlesAvailable;
    private bool _loggedUnavailable;
    private WindChargeLevel? _appliedLevel;
    private bool _disposed;

    /// <summary>06-A3: when to probe Moodles and retry a failed apply.</summary>
    private readonly MoodlesSyncGate _gate = new();

    /// <summary>06-A3: set by the IPC handlers (any thread), handled in <see cref="Tick"/> on the framework thread.</summary>
    private volatile bool _readyPending;
    private volatile bool _unloadPending;

    /// <summary>06-A12: Moodles' Ready asked for the status to be applied again; the next <see cref="SyncDesiredState"/> does.</summary>
    private bool _forceApplyPending;

    /// <summary>Whether the last <see cref="SyncDesiredState"/> wanted the status (on, a doll); losing it asks for a clear.</summary>
    private bool _wasWanted;

    /// <summary>06-A3: Moodles assemblies that have unloaded, held weakly so they can be collected; rebinding skips them.</summary>
    private readonly List<WeakReference<Assembly>> _unloadedAssemblies = [];

    private Assembly? _moodlesAsm;
    private Type? _myStatusType;
    private Type? _updateSourceType;
    private MethodInfo? _getMyStatusManager;
    private MethodInfo? _prepareToApply;
    private MethodInfo? _addOrUpdate;
    private MethodInfo? _cancelGuid;
    private MethodInfo? _containsGuid;
    private FieldInfo? _moodlesPlugin;
    private MethodInfo? _applyAutomation;

    /// <summary>
    /// 06-A13: used with Moodles' status type, so it caches that type's metadata. Created per bind and dropped in
    /// <see cref="ForgetReflection"/> (no longer static), so an unloaded Moodles assembly can be collected.
    /// </summary>
    private JsonSerializerOptions? _jsonOptions;

    public MoodlesWindStatusService(
        IDalamudPluginInterface pi,
        IClientState clientState,
        IObjectTable objects,
        Configuration config,
        WindTimerService timer,
        LowWindMessagesConfig messages,
        IPluginLog log)
    {
        _clientState = clientState;
        _objects = objects;
        _config = config;
        _timer = timer;
        _messages = messages;
        _log = log;

        _ready = pi.GetIpcSubscriber<object>("Moodles.Ready");
        _unloading = pi.GetIpcSubscriber<object>("Moodles.Unloading");
        _version = pi.GetIpcSubscriber<int>("Moodles.Version");

        _ready.Subscribe(OnMoodlesReady);
        _unloading.Subscribe(OnMoodlesUnloading);
        // 06-A3: no probe or reflection here: Tick probes only once the status is wanted (settings checked first).
    }

    public void Tick()
    {
        if (_disposed)
            return;

        try
        {
            // 06-A12: handles the pending IPC signals first, before anything calls into Moodles.
            SyncDesiredState();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Moodles wind status tick failed");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            _ready.Unsubscribe(OnMoodlesReady);
            _unloading.Unsubscribe(OnMoodlesUnloading);
        }
        catch
        {
            // Ignore unsubscribe failures during teardown.
        }

        try
        {
            // 06-A12: a pending unload drops the old handles first, so nothing is cancelled through them.
            HandleIpcSignals();
            // 06-A3: forced only if Moodles was found, so an unload without Moodles (or with the status off) touches nothing.
            ClearApplied(force: _moodlesAvailable);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Moodles wind status clear on dispose failed");
        }
    }

    /// <summary>
    /// Call once login has left BetweenAreas. Re-applies wind charge moodle if Moodles
    /// does not already have the desired status (e.g. after logout wiped local moodles).
    /// </summary>
    public void OnLoggedIn()
    {
        if (_disposed)
            return;

        try
        {
            // 06-A12: an unload seen since the last tick drops the old handles before anything calls into Moodles.
            HandleIpcSignals();
            // 06-A3: a login tries again at once, whatever the backoff.
            _gate.ResetBackoff();
            // A permanent wind moodle from an earlier session (status since turned off, or now a winder) stays visible
            // to others; one clear per login removes it. Acts only while the status isn't wanted.
            _gate.RequestClear();
            SyncDesiredState(forceApply: true, skipIfPresent: true);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Moodles wind status login apply failed");
        }
    }

    /// <summary>
    /// Call after leaving BetweenAreas. Re-applies the desired status because Moodles can
    /// retain stale manager state while no longer displaying the status after a transition.
    /// </summary>
    public void OnAreaTransition()
    {
        if (_disposed)
            return;

        try
        {
            // 06-A12: an unload seen since the last tick drops the old handles before anything calls into Moodles.
            HandleIpcSignals();

            // Before the wind status, so a ReplaceAll automation preset cannot drop it.
            if (_config.ReapplyMoodlesAutomationOnAreaChange)
                TryReapplyAutomation();

            SyncDesiredState(forceApply: true);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Moodles wind status area transition apply failed");
        }
    }

    /// <summary>06-A3: IPC handler, on whichever thread Moodles raises it: only sets a flag for <see cref="Tick"/>.</summary>
    private void OnMoodlesReady() => _readyPending = true;

    /// <summary>06-A3: IPC handler, on whichever thread Moodles raises it: only sets a flag for <see cref="Tick"/>.</summary>
    private void OnMoodlesUnloading() => _unloadPending = true;

    /// <summary>
    /// 06-A13: the doll turned the status off in the UI. Only asks for the one probe and clear, which the next
    /// <see cref="Tick"/> does on the framework thread.
    /// </summary>
    public void OnStatusTurnedOff() => _gate.RequestClear();

    /// <summary>
    /// 06-A3: framework thread. Applies Moodles' Unloading, then Ready (a reload raises both). Unloading forgets every
    /// reflection handle and keeps only a weak reference to the old assembly; Ready re-probes, rebinds lazily, resets
    /// the backoff, and asks for the status to be applied again (<see cref="_forceApplyPending"/>).
    /// 06-A12: called first by every entry point that may call into Moodles (<see cref="OnLoggedIn"/>,
    /// <see cref="OnAreaTransition"/>, <see cref="TryReapplyAutomation"/>, <see cref="SyncDesiredState"/>,
    /// <see cref="Dispose"/>), so no call goes through handles from before an unload. Nothing pending: no-op.
    /// </summary>
    private void HandleIpcSignals()
    {
        if (_unloadPending)
        {
            _unloadPending = false;
            if (_moodlesAsm is not null)
                _unloadedAssemblies.Add(new WeakReference<Assembly>(_moodlesAsm));
            _unloadedAssemblies.RemoveAll(w => !w.TryGetTarget(out _));
            ForgetReflection();
            _moodlesAvailable = false;
            _appliedLevel = null;
        }

        if (!_readyPending)
            return;

        _readyPending = false;
        _loggedUnavailable = false;
        // Probe again (the version may have changed) and bind to the assembly that is loaded now.
        ForgetReflection();
        _moodlesAvailable = false;
        _gate.OnMoodlesReady();
        _forceApplyPending = true;
        // Moodles loaded after the login's clear found it unavailable: clear a leftover now (only if not wanted).
        _gate.RequestClear();
    }

    /// <summary>06-A3: drops every cached reflection handle, including the assembly itself.</summary>
    private void ForgetReflection()
    {
        _moodlesAsm = null;
        _myStatusType = null;
        _updateSourceType = null;
        _getMyStatusManager = null;
        _prepareToApply = null;
        _addOrUpdate = null;
        _cancelGuid = null;
        _containsGuid = null;
        _moodlesPlugin = null;
        _applyAutomation = null;
        _jsonOptions = null;
    }

    private bool IsUnloadedAssembly(Assembly assembly)
    {
        foreach (var weak in _unloadedAssemblies)
        {
            if (weak.TryGetTarget(out var old) && ReferenceEquals(old, assembly))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Runs Moodles' own <c>ApplyAutomation</c> for the local player, so Moodles still decides
    /// which profiles and presets match.
    /// </summary>
    private void TryReapplyAutomation()
    {
        try
        {
            // 06-A12: an unload seen since the last tick drops the old handles before anything calls into Moodles.
            HandleIpcSignals();
            if (!_clientState.IsLoggedIn || _objects.LocalPlayer is null)
                return;

            // 06-A3: its own setting was checked by the caller; the probe still runs at most every 10 s.
            if (!_moodlesAvailable && _gate.TryStartProbe(DateTime.UtcNow))
                TryProbeMoodles();
            if (!_moodlesAvailable)
                return;

            if (_applyAutomation is null || _moodlesPlugin is null)
                TryBindMoodlesReflection();

            var plugin = _moodlesPlugin?.GetValue(null);
            if (_applyAutomation is null || plugin is null)
            {
                LogUnavailableOnce("Moodles ApplyAutomation not found; automation re-apply unavailable");
                return;
            }

            _applyAutomation.Invoke(plugin, [false]);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Moodles automation re-apply failed");
        }
    }

    private void TryProbeMoodles()
    {
        try
        {
            var version = _version.InvokeFunc();
            _moodlesAvailable = version >= MinMoodlesApiVersion;
            if (!_moodlesAvailable && !_loggedUnavailable)
            {
                _loggedUnavailable = true;
                _log.Information("Moodles IPC version {Version} is below required {Required}; wind charge moodles disabled",
                    version, MinMoodlesApiVersion);
            }
        }
        catch (IpcNotReadyError)
        {
            _moodlesAvailable = false;
        }
        catch (Exception ex)
        {
            _moodlesAvailable = false;
            if (!_loggedUnavailable)
            {
                _loggedUnavailable = true;
                _log.Debug(ex, "Moodles IPC probe failed; wind charge moodles unavailable");
            }
        }
    }

    private void SyncDesiredState(bool forceApply = false, bool skipIfPresent = false)
    {
        // 06-A12: an unload seen since the last tick drops the old handles before anything calls into Moodles; a Ready
        // asks for the status again.
        HandleIpcSignals();
        if (_forceApplyPending)
        {
            _forceApplyPending = false;
            forceApply = true;
        }

        // Turned off by any path, or the role changed (switched to winder, remote settings): ask for the clear below.
        var wanted = _config.MoodlesStatusEnabled && _config.IsDoll;
        if (_wasWanted && !wanted)
            _gate.RequestClear();
        _wasWanted = wanted;

        // 06-A13: turned off in the UI: one probe and clear, even of a status we don't remember applying.
        if (_gate.TakeClearRequest(_config.MoodlesStatusEnabled, _config.IsDoll, _clientState.IsLoggedIn))
            ClearAfterTurnedOff();

        // 06-A3: the settings, the role and the login are checked before any IPC probe, and a probe runs at most
        // every 10 s while Moodles is unavailable.
        var now = DateTime.UtcNow;
        var step = _gate.Next(_config.MoodlesStatusEnabled, _config.IsDoll, _clientState.IsLoggedIn, _moodlesAvailable, now);
        if (step == MoodlesStep.Off)
        {
            ClearApplied();
            return;
        }

        if (step == MoodlesStep.Probe)
            TryProbeMoodles();

        if (!_moodlesAvailable)
        {
            ClearApplied();
            return;
        }

        // Avoid Moodles reflection while the local player object is still settling in.
        if (_objects.LocalPlayer is not IPlayerCharacter player)
        {
            ClearApplied();
            return;
        }

        var level = ResolveLevel(_timer.RemainingForWinder());
        if (!forceApply && _appliedLevel == level)
            return;

        // 06-A3: after a failed apply or rebind, wait (5 s doubling to 5 min) instead of rebinding every frame. A new
        // level, a login and Moodles' Ready try again at once.
        if (!_gate.MayApply(level, now))
            return;

        if (skipIfPresent && TryContainsStatus(player, LevelMeta(level).Guid))
        {
            _appliedLevel = level;
            _gate.ApplySucceeded();
            return;
        }

        ApplyLevel(level, player);
        if (_appliedLevel == level)
            _gate.ApplySucceeded();
        else
            _gate.ApplyFailed(now);
    }

    /// <summary>
    /// 06-A13: once per turn-off, on the framework thread. Probes Moodles if it isn't known to be available (whatever
    /// the probe interval) and, if it is, cancels our wind statuses even when <see cref="_appliedLevel"/> is null (a
    /// permanent one left from an earlier session or load). Never repeated while the status stays off. 06-A15: with no
    /// local player yet, asks again for the next tick instead of spending the request without clearing.
    /// </summary>
    private void ClearAfterTurnedOff()
    {
        if (_objects.LocalPlayer is not IPlayerCharacter)
        {
            _gate.RequestClear();
            return;
        }

        if (!_moodlesAvailable)
            TryProbeMoodles();
        if (_moodlesAvailable)
            ClearApplied(force: true);
    }

    private void ClearApplied(bool force = false)
    {
        if (!force && _appliedLevel is null)
            return;

        if (_objects.LocalPlayer is IPlayerCharacter player)
            TryCancelAll(player);

        _appliedLevel = null;
    }

    private void TryBindMoodlesReflection()
    {
        try
        {
            // 06-A3: never an assembly that has unloaded (it can stay listed until collected); the newest one otherwise.
            _moodlesAsm = AppDomain.CurrentDomain.GetAssemblies()
                .LastOrDefault(a => string.Equals(a.GetName().Name, "Moodles", StringComparison.Ordinal)
                                    && !IsUnloadedAssembly(a));
            if (_moodlesAsm is null)
            {
                _addOrUpdate = null;
                _containsGuid = null;
                return;
            }

            Type? FindType(string simpleName) =>
                _moodlesAsm.GetTypes().FirstOrDefault(t => t.Name == simpleName);

            // Plugin singleton Moodles.P and its public ApplyAutomation(bool forceOtherPlayers).
            var pluginType = FindType("Moodles");
            _moodlesPlugin = pluginType?.GetField("P", BindingFlags.Public | BindingFlags.Static);
            _applyAutomation = pluginType?.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m =>
                    m.Name == "ApplyAutomation"
                    && m.GetParameters() is { Length: 1 } p
                    && p[0].ParameterType == typeof(bool));

            _myStatusType = FindType("MyStatus");
            var utilsType = FindType("Utils");
            _updateSourceType = FindType("UpdateSource");
            var managerType = FindType("MyStatusManager");
            var prepareOptionsType = FindType("PrepareOptions");

            if (_myStatusType is null || utilsType is null || _updateSourceType is null || managerType is null
                || prepareOptionsType is null)
            {
                _addOrUpdate = null;
                _containsGuid = null;
                return;
            }

            // 06-A13: new options per bind, so no metadata of an earlier Moodles assembly's types is kept. 06-A15: with
            // their own resolver, so System.Text.Json's cache shared by equal options can't keep the old assembly alive
            // after a quick reload.
            _jsonOptions = new JsonSerializerOptions
            {
                IncludeFields = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            };

            _getMyStatusManager = utilsType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m =>
                    m.Name == "GetMyStatusManager"
                    && m.GetParameters() is { Length: 2 } p
                    && p[0].ParameterType == typeof(string)
                    && p[1].ParameterType == typeof(bool));

            _prepareToApply = utilsType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m =>
                    m.Name == "PrepareToApply"
                    && m.GetParameters() is { Length: 2 } p
                    && p[0].ParameterType == _myStatusType
                    && p[1].ParameterType == prepareOptionsType.MakeArrayType());

            _addOrUpdate = managerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m =>
                    m.Name == "AddOrUpdate"
                    && m.GetParameters() is { Length: 4 } p
                    && p[0].ParameterType == _myStatusType
                    && p[1].ParameterType == _updateSourceType
                    && p[2].ParameterType == typeof(bool)
                    && p[3].ParameterType == typeof(bool));

            _cancelGuid = managerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m =>
                    m.Name == "Cancel"
                    && m.GetParameters() is { Length: 2 } p
                    && p[0].ParameterType == typeof(Guid)
                    && p[1].ParameterType == typeof(bool));

            _containsGuid = managerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m =>
                    m.Name == "ContainsStatus"
                    && m.GetParameters() is { Length: 1 } p
                    && p[0].ParameterType == typeof(Guid)
                    && m.ReturnType == typeof(bool));
        }
        catch (Exception ex)
        {
            _addOrUpdate = null;
            _containsGuid = null;
            _log.Debug(ex, "Moodles reflection bind failed");
        }
    }

    private void ApplyLevel(WindChargeLevel level, IPlayerCharacter player)
    {
        var (guid, iconId, statusType) = LevelMeta(level);
        var (title, description) = LevelText(level);
        var applier = TryLocalApplier(player) ?? string.Empty;
        if (string.IsNullOrEmpty(applier))
        {
            _appliedLevel = null;
            return;
        }

        // Moodles JSON MyStatus shape; NoExpire + AsPermanent → no countdown, survives zone.
        var payload = new MoodleStatusPayload
        {
            GUID = guid,
            IconID = iconId,
            Title = title,
            Description = description,
            CustomFXPath = string.Empty,
            ExpiresAt = 0,
            Type = statusType,
            Modifiers = 0,
            Stacks = 1,
            StackSteps = 0,
            ChainedStatus = Guid.Empty,
            ChainTrigger = 0,
            Applier = applier,
            Dispeller = string.Empty,
            NoExpire = true,
            AsPermanent = true,
        };

        try
        {
            if (_addOrUpdate is null)
                TryBindMoodlesReflection();
            if (_addOrUpdate is null || _getMyStatusManager is null || _prepareToApply is null
                || _myStatusType is null || _updateSourceType is null || _jsonOptions is null)
            {
                _appliedLevel = null;
                return;
            }

            var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, _jsonOptions));
            var statusObj = JsonSerializer.Deserialize(jsonBytes, _myStatusType, _jsonOptions);
            if (statusObj is null)
            {
                _appliedLevel = null;
                return;
            }

            var prepareOptionsType = _prepareToApply.GetParameters()[1].ParameterType.GetElementType()!;
            var emptyOpts = Array.CreateInstance(prepareOptionsType, 0);
            var prepared = _prepareToApply.Invoke(null, [statusObj, emptyOpts]);
            if (prepared is null)
            {
                _appliedLevel = null;
                return;
            }

            var manager = _getMyStatusManager.Invoke(null, [applier, true]);
            if (manager is null)
            {
                _appliedLevel = null;
                return;
            }

            CancelAllOnManager(manager);

            var statusTuple = Enum.Parse(_updateSourceType, "StatusTuple");
            var added = _addOrUpdate.Invoke(manager, [prepared, statusTuple, false, true]);
            _appliedLevel = added is not null ? level : null;
            if (_appliedLevel is null)
                LogUnavailableOnce("Moodles local AddOrUpdate did not retain wind charge moodle");
        }
        catch (Exception ex)
        {
            _appliedLevel = null;
            LogUnavailableOnce("Failed to apply wind charge moodle via Moodles local manager", ex);
        }
    }

    private void TryCancelAll(IPlayerCharacter player)
    {
        try
        {
            if (_cancelGuid is null || _getMyStatusManager is null)
                TryBindMoodlesReflection();
            if (_cancelGuid is null || _getMyStatusManager is null)
                return;

            var applier = TryLocalApplier(player);
            if (string.IsNullOrEmpty(applier))
                return;

            var manager = _getMyStatusManager.Invoke(null, [applier, false]);
            if (manager is null)
                return;

            CancelAllOnManager(manager);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Failed to cancel wind charge moodles");
        }
    }

    private void CancelAllOnManager(object manager)
    {
        if (_cancelGuid is null)
            return;

        foreach (var oldGuid in AllLevelGuids)
            _cancelGuid.Invoke(manager, [oldGuid, true]);
    }

    private bool TryContainsStatus(IPlayerCharacter player, Guid guid)
    {
        try
        {
            if (_containsGuid is null || _getMyStatusManager is null)
                TryBindMoodlesReflection();
            if (_containsGuid is null || _getMyStatusManager is null)
                return false;

            var applier = TryLocalApplier(player);
            if (string.IsNullOrEmpty(applier))
                return false;

            var manager = _getMyStatusManager.Invoke(null, [applier, false]);
            if (manager is null)
                return false;

            return _containsGuid.Invoke(manager, [guid]) is true;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Failed to query wind charge moodle presence");
            return false;
        }
    }

    private void LogUnavailableOnce(string message, Exception? ex = null)
    {
        if (_loggedUnavailable)
            return;
        _loggedUnavailable = true;
        if (ex is null)
            _log.Information(message);
        else
            _log.Information(ex, message);
    }

    private static string? TryLocalApplier(IPlayerCharacter player)
    {
        try
        {
            var world = player.HomeWorld.ValueNullable?.Name.ToString();
            if (string.IsNullOrEmpty(world))
                return null;
            var name = player.Name.TextValue;
            if (string.IsNullOrWhiteSpace(name))
                return null;
            return $"{name}@{world}";
        }
        catch
        {
            return null;
        }
    }

    internal static WindChargeLevel ResolveLevel(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
            return WindChargeLevel.Unwound;
        if (remaining > FullyWoundMin)
            return WindChargeLevel.FullyWound;
        if (remaining > WoundMin)
            return WindChargeLevel.Wound;
        if (remaining > LowMin)
            return WindChargeLevel.Low;
        return WindChargeLevel.NearlySpent;
    }

    private static (Guid Guid, int IconId, int StatusType) LevelMeta(WindChargeLevel level) => level switch
    {
        WindChargeLevel.FullyWound => (FullyWoundGuid, 214146, 0), // Positive
        WindChargeLevel.Wound => (WoundGuid, 214142, 0),
        WindChargeLevel.Low => (LowGuid, 214141, 1), // Negative
        WindChargeLevel.NearlySpent => (NearlySpentGuid, 214140, 1),
        _ => (UnwoundGuid, 215260, 1),
    };

    private (string Title, string Description) LevelText(WindChargeLevel level) => level switch
    {
        WindChargeLevel.FullyWound => (_messages.MoodleFullyWoundTitle, _messages.MoodleFullyWoundDescription),
        WindChargeLevel.Wound => (_messages.MoodleWoundTitle, _messages.MoodleWoundDescription),
        WindChargeLevel.Low => (_messages.MoodleLowTitle, _messages.MoodleLowDescription),
        WindChargeLevel.NearlySpent => (_messages.MoodleNearlySpentTitle, _messages.MoodleNearlySpentDescription),
        _ => (_messages.MoodleUnwoundTitle, _messages.MoodleUnwoundDescription),
    };

    /// <summary>Field layout mirrors Moodles MyStatus for JSON deserialization into Moodles types.</summary>
    private sealed class MoodleStatusPayload
    {
        public Guid GUID;
        public int IconID;
        public string Title = "";
        public string Description = "";
        public string CustomFXPath = "";
        public long ExpiresAt;
        public int Type;
        public int Modifiers;
        public int Stacks = 1;
        public int StackSteps;
        public Guid ChainedStatus;
        public int ChainTrigger;
        public string Applier = "";
        public string Dispeller = "";
        public bool NoExpire;
        public bool AsPermanent;
    }
}
