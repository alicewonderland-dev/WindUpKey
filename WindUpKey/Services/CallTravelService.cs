#if WINDUP_TESTING
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using WindUpKey.Protocol;
using WindUpKey.Ui;

namespace WindUpKey.Services;

/// <summary>
/// Testing-only: answers an owner call by traveling near the owner's position via Lifestream + vnavmesh.
/// </summary>
public sealed class CallTravelService : IDisposable
{
    private const string CallErrorEcho =
        "Your owner's call encountered an error. This feature is still in testing.";
    private const float CloseRangeYalms = 2f;
    private const float ArrivedSlopYalms = 0.25f;
    /// <summary>Short final legs are quicker and more reliable on the ground, especially into housing lots.</summary>
    private const float PreferWalkingWithinYalms = 30f;
    /// <summary>
    /// Max XZ distance for same-instance vnav. Housing main↔subdivision is ~700 yalms apart
    /// in the same TerritoryType — never path across that.
    /// </summary>
    private const float MaxSameInstancePathYalms = 200f;
    private const float HouseEntranceInteractYalms = 3.5f;
    /// <summary>How far we will vnav toward a visible Entrance / plot door before giving up.</summary>
    private const float HouseEntranceApproachMaxYalms = 200f;
    /// <summary>GeneralAction row: Mount Roulette.</summary>
    private const uint GeneralActionMountRoulette = 9;
    private static readonly TimeSpan TravelStepTimeout = TimeSpan.FromMinutes(3);
    /// <summary>How long to wait for Lifestream TaskManager / cast to show progress after we request travel.</summary>
    private static readonly TimeSpan LifestreamStartGrace = TimeSpan.FromSeconds(3);
    /// <summary>Cooldown between Teleport / GoToHousing retries after an interrupt or no-op.</summary>
    private static readonly TimeSpan TeleportRetryCooldown = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MountAttemptCooldown = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PathStartGrace = TimeSpan.FromSeconds(30);
    /// <summary>How long to keep trying Mount Roulette before falling back to foot pathing.</summary>
    private static readonly TimeSpan MountGrace = TimeSpan.FromSeconds(12);
    /// <summary>Short retry guard for entrance targeting/interact; confirmation is checked every tick.</summary>
    private static readonly TimeSpan HouseEnterAttemptCooldown = TimeSpan.FromMilliseconds(250);

    // Event object names for private house doors (Lifestream Lang.Entrance).
    private static readonly string[] HouseEntranceNames =
    [
        "Entrance",
        "ハウスへ入る",
        "进入房屋",
        "進入房屋",
        "Eingang",
        "Entrée",
        "주택으로 들어가기",
    ];

    private static readonly string[] HouseEnterConfirmText =
    [
        "Enter the estate hall?",
        "「ハウス」へ入りますか？",
        "要进入这间房屋吗？",
        "要進入這間房屋嗎？",
        "Das Gebäude betreten?",
        "Entrer dans la maison ?",
        "'주택'으로 들어가시겠습니까?",
    ];

    // Lifestream ResidentialAethernet.ApartmentSubdivisionAetherytes — used to hop main→subdivision.
    private static readonly Dictionary<int, uint> SubdivisionAetheryteByCity = new()
    {
        [HousingCallLocation.CityLimsa] = 1966096, // Mist — The Topmast Subdivision
        [HousingCallLocation.CityUldah] = 1966128, // Goblet — The Sultana's Breath Subdivision
        [HousingCallLocation.CityGridania] = 1966112, // Lavender Beds — Lily Hills Subdivision
        [HousingCallLocation.CityKugane] = 1966142, // Shirogane — Kobai Goten Subdivision
        [HousingCallLocation.CityFoundation] = 1966157, // Empyreum — Ingleside Subdivision
    };

    private readonly IDalamudPluginInterface _pi;
    private readonly IClientState _clientState;
    private readonly IObjectTable _objects;
    private readonly ITargetManager _targets;
    private readonly IGameGui _gameGui;
    private readonly ICondition _condition;
    private readonly IDataManager _data;
    private readonly IChatGui _chat;
    private readonly IPluginLog _log;
    private readonly Configuration _config;
    private readonly WindTimerService _timer;
    private readonly CallPromptWindow _prompt;
    private readonly Func<CallAckPayload, Task> _sendAck;
    private readonly Func<CallResultPayload, Task> _sendResult;

    private ICallGateSubscriber<bool>? _lsIsBusy;
    private ICallGateSubscriber<object>? _lsAbort;
    private ICallGateSubscriber<uint, bool>? _lsChangeWorldById;
    private ICallGateSubscriber<string, bool>? _lsChangeWorld;
    private ICallGateSubscriber<uint, byte, bool>? _lsTeleport;
    /// <summary>Dalamud IPC: Action&lt;T&gt; is bound as Subscriber&lt;T, object&gt; and invoked via InvokeAction.</summary>
    private ICallGateSubscriber<(string, int, int, int, int, int, int, bool, bool, string), object>? _lsGoToHousing;
    private ICallGateSubscriber<string, string, string, string, bool, bool, (string, int, int, int, int, int, int, bool, bool, string)>? _lsBuildAddress;
    private ICallGateSubscriber<uint, bool>? _lsHousingAethernetById;
    private ICallGateSubscriber<uint, int, Vector3?>? _lsGetPlotEntrance;
    private ICallGateSubscriber<bool>? _vnavReady;
    private ICallGateSubscriber<Vector3, bool, float, bool>? _vnavMoveCloseTo;
    private ICallGateSubscriber<bool>? _vnavPathRunning;
    private ICallGateSubscriber<bool>? _vnavPathfindInProgress;
    private ICallGateSubscriber<object>? _vnavPathStop;

    private bool _disposed;
    private bool _ipcBound;
    private PendingCall? _pending;
    private bool _weOwnTravel;
    private bool _craftNotified;
    private bool _subdivisionHopAttempted;
    private bool _housingCityTeleportAttempted;
    private bool _housingGoRetryAttempted;
    private bool _teleportProgressSeen;
    /// <summary>
    /// Accept is raised from the prompt's Draw callback. Defer IPC, automation, and input-hook
    /// state changes to the framework tick instead of performing native work while ImGui is drawing.
    /// </summary>
    private bool _acceptRequested;
    private bool _houseEntrancePathStarted;
    private bool _awaitingHouseEnterConfirm;
    private bool _pathingSeenBusy;
    private DateTimeOffset _lastMountAttemptUtc;
    private DateTimeOffset _lastHouseEnterAttemptUtc;
    private DateTimeOffset _houseEnterInteractedUtc;
    private DateTimeOffset _lastTeleportRetryUtc;
    private DateTimeOffset? _pathStartDeadlineUtc;
    private DateTimeOffset? _mountDeadlineUtc;
    private bool _localDebugCall;
    private CallPayload? _debugStoredPoint;
    private DateTimeOffset _stepStartedUtc;
    private TravelPhase _phase = TravelPhase.Idle;
    private string? _lastCallDebugKey;
    private DateTimeOffset _lastCallDebugUtc;

    private enum TravelPhase
    {
        Idle,
        WaitingGates,
        WaitingAccept,
        ChangingWorld,
        Teleporting,
        Pathing,
    }

    private sealed class PendingCall
    {
        public required string RequestId { get; init; }
        public required string OwnerKey { get; init; }
        public required uint WorldId { get; init; }
        public required string WorldName { get; init; }
        public required uint TerritoryId { get; init; }
        public required Vector3 Position { get; init; }
        public int HousingCity { get; init; }
        public int HousingWard { get; init; }
        public int HousingDivision { get; init; }
        public int HousingPlot { get; init; }
        public int HousingApartment { get; init; }
        public bool HousingIsApartment { get; init; }
        public bool HousingIndoor { get; init; }

        public bool IsHousingCall =>
            HousingWard > 0 && HousingCity != 0 && HousingDivision is 1 or 2;
    }

    public CallTravelService(
        IDalamudPluginInterface pi,
        IClientState clientState,
        IObjectTable objects,
        ITargetManager targets,
        IGameGui gameGui,
        ICondition condition,
        IDataManager data,
        IChatGui chat,
        IPluginLog log,
        Configuration config,
        WindTimerService timer,
        CallPromptWindow prompt,
        Func<CallAckPayload, Task> sendAck,
        Func<CallResultPayload, Task> sendResult)
    {
        _pi = pi;
        _clientState = clientState;
        _objects = objects;
        _targets = targets;
        _gameGui = gameGui;
        _condition = condition;
        _data = data;
        _chat = chat;
        _log = log;
        _config = config;
        _timer = timer;
        _prompt = prompt;
        _sendAck = sendAck;
        _sendResult = sendResult;

        _prompt.OnAccept = OnPromptAccept;
        BindIpc();
    }

    public bool IsTravelReady => ProbeTravelReady();

    public bool HasActiveCall => _pending is not null;

    /// <summary>True when <see cref="TryStoreDebugPoint"/> has captured a recall target this session.</summary>
    public bool HasDebugStoredPoint => _debugStoredPoint is not null;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _prompt.OnAccept = null;
        _prompt.IsOpen = false;
        CancelInternal(sendResult: false);
    }

    public void CancelActiveCall()
    {
        if (_pending is null)
            return;
        _ = FinishAsync(CallResultStatuses.Cancelled, "Call cancelled.");
    }

    /// <summary>
    /// Debug: snapshot the local player's current call destination (position + housing ward/division).
    /// </summary>
    public bool TryStoreDebugPoint()
    {
        if (_disposed)
            return false;

        if (_objects.LocalPlayer is not { } player || !_clientState.IsLoggedIn)
        {
            PluginChat.PrintError(_chat, "You must be in the world to store a call point.");
            return false;
        }

        var world = player.CurrentWorld.ValueNullable;
        var territoryId = _clientState.TerritoryType;
        var stored = new CallPayload
        {
            RequestId = string.Empty,
            From = _config.PairingKey,
            To = _config.PairingKey,
            WorldId = player.CurrentWorld.RowId,
            WorldName = world?.Name.ToString() ?? string.Empty,
            TerritoryId = territoryId,
            X = player.Position.X,
            Y = player.Position.Y,
            Z = player.Position.Z,
        };

        if (HousingCallLocation.TryRead(territoryId, territoryRow: null, out var housing, _data))
        {
            stored.HousingCity = housing.City;
            stored.HousingWard = housing.Ward;
            stored.HousingDivision = housing.Division;
            stored.HousingPlot = housing.Plot;
            stored.HousingApartment = housing.Apartment;
            stored.HousingIsApartment = housing.IsApartment;
            stored.HousingIndoor = housing.Indoor;
            stored.TerritoryId = housing.OutdoorTerritoryId;
        }
        else
        {
            DebugCall(
                $"store point: HousingCallLocation.TryRead failed terr={territoryId} | {FormatLocalHousingSnapshot()}",
                throttle: false);
        }

        _debugStoredPoint = stored;
        PluginChat.Print(_chat, FormatDebugStoredSummary(stored), PluginChat.Green);
        return true;
    }

    /// <summary>
    /// Debug: start Call travel to the last stored point (local only — no relay).
    /// No-op when nothing has been stored.
    /// </summary>
    public bool TryRecallDebugPoint()
    {
        if (_disposed || _debugStoredPoint is null)
            return false;

        if (!_config.IsDoll)
        {
            PluginChat.PrintError(_chat, "Call recall is only available as a Doll.");
            return false;
        }

        var payload = CloneCallPayload(_debugStoredPoint);
        payload.RequestId = Guid.NewGuid().ToString("N");
        payload.From = PairingKeyUtil.IsValid(_config.PairingKey)
            ? _config.PairingKey
            : "DEBUGCALL";
        payload.To = payload.From;
        _ = RequestAsync(payload, localDebug: true);
        return true;
    }

    public Task RequestAsync(CallPayload payload) => RequestAsync(payload, localDebug: false);

    private async Task RequestAsync(CallPayload payload, bool localDebug)
    {
        if (_disposed || payload is null)
            return;

        if (_pending is not null)
        {
            ReportEdgeCaseError(
                "request rejected: already answering another call"
                + $" | incoming terr={payload.TerritoryId} world={payload.WorldId}");
            await SendResultOrLocalAsync(payload, CallResultStatuses.Failed, "Already answering another call.", localDebug)
                .ConfigureAwait(false);
            return;
        }

        // vnavmesh may report not-ready while combat/instance/crafting already prevents travel.
        // Preserve the Call and show its normal wait state; probe again when Accept can become active.
        var hasDeferredGameGate = IsCrafting() || IsInInstance() || IsInCombat();
        if (!hasDeferredGameGate && !IsTravelReady)
        {
            ReportEdgeCaseError(
                "request rejected: travel plugins unavailable"
                + $" | terr={payload.TerritoryId} world={payload.WorldId}");
            await SendResultOrLocalAsync(payload, CallResultStatuses.Failed, "Lifestream and vnavmesh are required.", localDebug)
                .ConfigureAwait(false);
            return;
        }

        if (!SameDataCenter(payload.WorldId))
        {
            ReportEdgeCaseError(
                "request rejected: cross-data-center travel"
                + $" | terr={payload.TerritoryId} world={payload.WorldId}");
            await SendResultOrLocalAsync(payload, CallResultStatuses.Failed, "Cannot travel across data centers.", localDebug)
                .ConfigureAwait(false);
            return;
        }

        var ownerKey = PairingKeyUtil.Normalize(payload.From);
        if (string.IsNullOrEmpty(ownerKey))
            ownerKey = localDebug ? "DEBUGCALL" : string.Empty;
        if (string.IsNullOrEmpty(ownerKey))
            return;

        _localDebugCall = localDebug;
        var housingPlot = payload.HousingPlot;
        var housingDivision = HousingCallLocation.EffectiveDivision(
            housingPlot,
            payload.HousingDivision,
            payload.HousingIsApartment);
        housingPlot = HousingCallLocation.ToLifestreamPlot(
            housingPlot,
            housingDivision,
            payload.HousingIsApartment);
        housingDivision = HousingCallLocation.EffectiveDivision(
            housingPlot,
            housingDivision,
            payload.HousingIsApartment);
        if (housingPlot <= 0
            && !payload.HousingIsApartment
            && payload.HousingWard > 0
            && payload.HousingCity != 0
            && housingDivision is 1 or 2)
        {
            housingPlot = FindNearestHousingPlot(
                payload.HousingCity,
                housingDivision,
                new Vector3(payload.X, payload.Y, payload.Z));
        }

        _pending = new PendingCall
        {
            RequestId = payload.RequestId,
            OwnerKey = ownerKey,
            WorldId = payload.WorldId,
            WorldName = payload.WorldName?.Trim() ?? string.Empty,
            TerritoryId = payload.TerritoryId,
            Position = new Vector3(payload.X, payload.Y, payload.Z),
            HousingCity = payload.HousingCity,
            HousingWard = payload.HousingWard,
            HousingDivision = housingDivision,
            HousingPlot = housingPlot,
            HousingApartment = payload.HousingApartment,
            HousingIsApartment = payload.HousingIsApartment,
            HousingIndoor = payload.HousingIndoor,
        };
        _craftNotified = false;
        _subdivisionHopAttempted = false;
        _housingCityTeleportAttempted = false;
        _housingGoRetryAttempted = false;
        _teleportProgressSeen = false;
        _houseEntrancePathStarted = false;
        _awaitingHouseEnterConfirm = false;
        _pathingSeenBusy = false;
        _lastMountAttemptUtc = default;
        _lastHouseEnterAttemptUtc = default;
        _houseEnterInteractedUtc = default;
        _lastTeleportRetryUtc = default;
        _pathStartDeadlineUtc = null;
        _mountDeadlineUtc = null;
        _weOwnTravel = false;
        _lastCallDebugKey = null;
        _phase = TravelPhase.WaitingGates;
        // Prior cancel/fail must not leave Lifestream mid-task or Accept stays dead.
        AbortCallAutomation();
        _timer.SetCallTravelBypass(true);

        DebugCall($"accepted request → {FormatPendingSummary(_pending)}", throttle: false);

        if (localDebug)
            PluginChat.Print(_chat, "Debug call recall started.", PluginChat.Yellow);

        var status = ClassifyGateStatus();
        await AckAsync(status).ConfigureAwait(false);
    }

    public void Tick()
    {
        if (_disposed || _pending is null)
            return;

        try
        {
            // After aetheryte / housing travel, LocalPlayer is briefly null while still logged in.
            // Treating that as logout cancelled the Call the moment the doll landed.
            if (!_clientState.IsLoggedIn)
            {
                _ = FinishAsync(CallResultStatuses.Cancelled, "Logged out.");
                return;
            }

            if (_objects.LocalPlayer is null)
                return;

            SyncLifestreamDrivingRmi();

            switch (_phase)
            {
                case TravelPhase.WaitingGates:
                    TickWaitingGates();
                    break;
                case TravelPhase.WaitingAccept:
                    TickWaitingAccept();
                    break;
                case TravelPhase.ChangingWorld:
                    TickChangingWorld();
                    break;
                case TravelPhase.Teleporting:
                    TickTeleporting();
                    break;
                case TravelPhase.Pathing:
                    _timer.SetCallTravelMuteDestination(_pending.Position);
                    TickPathing();
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Call travel tick failed");
            EnterFailedPrompt($"Unhandled tick exception: {ex}");
        }
    }

    private void TickWaitingGates()
    {
        if (IsCrafting())
        {
            if (!_craftNotified)
            {
                _craftNotified = true;
                PluginChat.Print(_chat, PresenceRequiredEcho(), PluginChat.Yellow);
                _ = AckAsync(CallAckStatuses.Crafting);
            }
            return;
        }

        if (IsInInstance())
        {
            _ = AckAsync(CallAckStatuses.Instance);
            return;
        }

        if (IsInCombat())
        {
            ShowPrompt(CallPromptReason.Combat, canAccept: false);
            _phase = TravelPhase.WaitingAccept;
            _ = AckAsync(CallAckStatuses.Combat);
            return;
        }

        if (IsExternallyBusy())
        {
            ShowPrompt(CallPromptReason.Busy, canAccept: false);
            _phase = TravelPhase.WaitingAccept;
            _ = AckAsync(CallAckStatuses.Busy);
            return;
        }

        BeginTravel();
    }

    private void TickWaitingAccept()
    {
        if (_pending is null)
            return;

        if (IsCrafting() || IsInInstance())
        {
            _prompt.IsOpen = false;
            _timer.SetCallTravelInputMute(false);
            _phase = TravelPhase.WaitingGates;
            return;
        }

        var reason = _prompt.Reason;
        var clear = reason switch
        {
            CallPromptReason.Combat => !IsInCombat() && !IsExternallyBusy() && IsTravelReady,
            CallPromptReason.Busy => !IsExternallyBusy() && !IsInCombat() && IsTravelReady,
            // Failed retries abort leftover automation on Accept — don't keep the button locked
            // because Lifestream is still draining from the attempt that just failed.
            CallPromptReason.Failed => !IsInCombat() && IsTravelReady,
            _ => !IsInCombat() && !IsExternallyBusy(),
        };

        _prompt.CanAccept = clear;

        if (_acceptRequested)
        {
            _acceptRequested = false;
            TryAcceptPendingCall();
            return;
        }

        if (!_prompt.IsOpen)
            _prompt.IsOpen = true;
    }

    private void OnPromptAccept()
    {
        if (_pending is null || !_prompt.CanAccept)
            return;

        // This callback runs during UI drawing. Native IPC, automation cancellation, and input
        // hook activation are handled by TickWaitingAccept on the next framework update.
        _acceptRequested = true;
        _prompt.IsOpen = false;
    }

    private void TryAcceptPendingCall()
    {
        if (_pending is null)
            return;

        // A fresh request was already cleaned when it was received. Repeating both synchronous
        // stop/abort IPC calls here causes a visible hitch immediately after combat. Failed retries
        // are the only Accept path where stale plugin task queues may still need another drain.
        if (_prompt.Reason == CallPromptReason.Failed)
            AbortCallAutomation();

        if (IsCrafting() || IsInInstance() || IsInCombat())
        {
            _phase = TravelPhase.WaitingGates;
            return;
        }

        // Readiness can change between drawing the button and handling its click.
        if (!IsTravelReady)
        {
            _prompt.IsOpen = true;
            _prompt.CanAccept = false;
            _phase = TravelPhase.WaitingAccept;
            return;
        }

        // After a failed attempt, leftover Lifestream/vnav must not bounce us back to Busy.
        if (_prompt.Reason != CallPromptReason.Failed && IsExternallyBusy())
        {
            _phase = TravelPhase.WaitingGates;
            return;
        }

        BeginTravel();
    }

    private void BeginTravel()
    {
        if (_pending is null)
            return;

        _prompt.IsOpen = false;
        _subdivisionHopAttempted = false;
        _housingCityTeleportAttempted = false;
        _housingGoRetryAttempted = false;
        _teleportProgressSeen = false;
        _lastMountAttemptUtc = default;
        _pathStartDeadlineUtc = null;
        _mountDeadlineUtc = null;
        _lastCallDebugKey = null;
        _ = AckAsync(CallAckStatuses.Traveling);
        // Mute input immediately, but do not steer toward the destination until local pathing —
        // otherwise we walk housing coords through the current zone's mesh (into walls).
        _timer.SetCallTravelInputMute(true);
        PluginChat.Print(_chat, PresenceRequiredEcho(), PluginChat.Yellow);
        DebugCall($"begin travel → {FormatPendingSummary(_pending)} | {FormatLocalHousingSnapshot()}", throttle: false);

        var localWorld = _objects.LocalPlayer?.CurrentWorld.RowId ?? 0;
        if (localWorld != 0 && localWorld != _pending.WorldId)
        {
            DebugCall($"change world {localWorld} → {_pending.WorldId}", throttle: false);
            if (!TryChangeWorld(_pending))
            {
                EnterFailedPrompt("Could not change world.");
                return;
            }

            _weOwnTravel = true;
            _phase = TravelPhase.ChangingWorld;
            _stepStartedUtc = DateTimeOffset.UtcNow;
            return;
        }

        StartTerritoryOrPath();
    }

    private void TickChangingWorld()
    {
        if (_pending is null)
            return;

        if (DateTimeOffset.UtcNow - _stepStartedUtc > TravelStepTimeout)
        {
            EnterFailedPrompt();
            return;
        }

        if (IsInInstance() || IsBetweenAreas())
            return;

        var localWorld = _objects.LocalPlayer?.CurrentWorld.RowId ?? 0;
        if (localWorld == _pending.WorldId && !IsLifestreamBusy())
        {
            _weOwnTravel = false;
            StartTerritoryOrPath();
        }
    }

    private void StartTerritoryOrPath()
    {
        if (_pending is null)
            return;

        if (CanStartLocalPath())
        {
            DebugCall($"start: can path locally | {FormatPathGateSummary()}", throttle: false);

            // Wait out groundsit get-up before mount/path — otherwise vnav runs in place.
            if (_condition[ConditionFlag.InThatPosition])
            {
                DebugCall("start: waiting for groundsit get-up", key: "start-sit");
                _weOwnTravel = true;
                _phase = TravelPhase.Teleporting;
                _pathStartDeadlineUtc ??= DateTimeOffset.UtcNow + PathStartGrace;
                _stepStartedUtc = DateTimeOffset.UtcNow;
                return;
            }

            if (IsVnavPathfindPending())
            {
                DebugCall("start: waiting for leftover vnav pathfind", key: "start-vnav-pending");
                _weOwnTravel = true;
                _phase = TravelPhase.Teleporting;
                _pathStartDeadlineUtc ??= DateTimeOffset.UtcNow + PathStartGrace;
                _stepStartedUtc = DateTimeOffset.UtcNow;
                return;
            }

            if (IsVnavPathRunning())
                StopVnavPath();

            if (!TryEnsureMountedForPath())
            {
                DebugCall("start: waiting for mount", key: "start-mount");
                _weOwnTravel = true;
                _phase = TravelPhase.Teleporting;
                _stepStartedUtc = DateTimeOffset.UtcNow;
                return;
            }

            if (!TryStartLocalPath())
            {
                DebugCall($"start: path start deferred | {DescribePathStartFailure()}", key: "start-path-defer");
                // Mesh often not ready on the first tick in-zone — keep trying in Teleporting.
                _weOwnTravel = true;
                _phase = TravelPhase.Teleporting;
                _pathStartDeadlineUtc ??= DateTimeOffset.UtcNow + PathStartGrace;
                _stepStartedUtc = DateTimeOffset.UtcNow;
                return;
            }

            DebugCall("start: pathing begun", throttle: false);
            return;
        }

        DebugCall($"start: cannot path yet | {FormatPathGateSummary()}", throttle: false);

        // Same housing territory/ward flag but far away → wrong division instance; hop before pathing.
        if (_pending.IsHousingCall && IsAtHousingWard() && !IsWithinSameInstancePathRange())
        {
            if (_pending.HousingDivision == 2)
                TryHopToSubdivisionIfNeeded();
            if (!IsWithinSameInstancePathRange() && !TryGoToHousing(_pending))
            {
                EnterFailedPrompt("Could not start housing travel (wrong division).");
                return;
            }

            _weOwnTravel = true;
            _phase = TravelPhase.Teleporting;
            _stepStartedUtc = DateTimeOffset.UtcNow;
            return;
        }

        if (_pending.IsHousingCall)
        {
            DebugCall("start: GoToHousingAddress", throttle: false);
            if (!TryGoToHousing(_pending))
            {
                EnterFailedPrompt("Lifestream GoToHousingAddress failed.");
                return;
            }
        }
        else if (!TryTeleportNear(_pending.TerritoryId, _pending.Position))
        {
            EnterFailedPrompt("Could not find a nearby aetheryte.");
            return;
        }

        _weOwnTravel = true;
        _phase = TravelPhase.Teleporting;
        _stepStartedUtc = DateTimeOffset.UtcNow;
    }

    private void TickTeleporting()
    {
        if (_pending is null)
            return;

        // Lifestream remains busy while waiting on the selected apartment's confirmation.
        // Confirm before the general busy early-return or the Call can wait here forever.
        if (_pending.IsHousingCall
            && _pending.HousingIndoor
            && _pending.HousingIsApartment
            && IsAtHousingWard()
            && TryConfirmHousingEntranceYesno(apartment: true, out var apartmentYesnoDetail))
        {
            _teleportProgressSeen = true;
            DebugCall($"enter-apartment: confirmed SelectYesno ({apartmentYesnoDetail})", key: "enter-apartment-yesno");
            return;
        }

        if (DateTimeOffset.UtcNow - _stepStartedUtc > TravelStepTimeout)
        {
            EnterFailedPrompt("Travel timed out.");
            return;
        }

        // Aetheryte Teleport does not set Lifestream.IsBusy — only the cast + BetweenAreas.
        if (IsBetweenAreas() || IsLifestreamBusy() || IsPlayerCasting())
        {
            _teleportProgressSeen = true;
            DebugCall(
                $"teleport: in transit (betweenAreas={IsBetweenAreas()} lsBusy={IsLifestreamBusy()} casting={IsPlayerCasting()}) | {FormatLocalHousingSnapshot()}",
                key: "teleport-busy");
            return;
        }

        if (_pending.IsHousingCall)
        {
            if (!IsAtHousingWard())
            {
                DebugCall(
                    $"teleport: not at housing ward yet | {FormatPathGateSummary()}",
                    key: "teleport-not-ward");
                TryHopToSubdivisionIfNeeded();
                // Interrupted cast / cancelled Lifestream must not end the Call — keep retrying.
                if (DateTimeOffset.UtcNow - _stepStartedUtc > LifestreamStartGrace)
                    TryRestartInterruptedTeleport();

                return;
            }

            // Indoor Calls: walk to the door, enter, then stop. Do not vnav inside —
            // customizable interiors routinely trap pathfinding.
            if (_pending.HousingIndoor)
            {
                if (!IsInsideOwnerProperty())
                {
                    DebugCall(
                        $"teleport: at ward, entering house | {FormatPathGateSummary()}",
                        key: "teleport-wait-enter");
                    if (!IsWithinSameInstancePathRange() && TryHopToSubdivisionIfNeeded())
                    {
                        _teleportProgressSeen = true;
                        return;
                    }

                    TryProgressIndoorHouseEntry();

                    if (!_teleportProgressSeen
                        && DateTimeOffset.UtcNow - _stepStartedUtc > LifestreamStartGrace
                        && !TryRecoverStalledHousingTravel())
                    {
                        EnterFailedPrompt("Could not enter the owner's house.");
                    }

                    return;
                }

                StopOurPath();
                DebugCall("teleport: entered house — arrived (no indoor pathing)", throttle: false);
                _ = FinishAsync(CallResultStatuses.Arrived, "Arrived at owner's house.");
                return;
            }

            if (!IsWithinSameInstancePathRange())
            {
                DebugCall(
                    $"teleport: at ward but out of path range | {FormatPathGateSummary()}",
                    key: "teleport-range");
                // Ward matched but XZ is subdivision-offset away — finish the hop before vnav.
                if (TryHopToSubdivisionIfNeeded())
                {
                    _teleportProgressSeen = true;
                    return;
                }

                if (DateTimeOffset.UtcNow - _stepStartedUtc > LifestreamStartGrace)
                    TryRestartInterruptedTeleport();

                return;
            }
        }
        else if (_clientState.TerritoryType != _pending.TerritoryId)
        {
            // Still away from destination — retry Teleport if the cast was interrupted.
            // Do not fail the Call for a cancelled cast; only the overall step timeout ends it.
            if (DateTimeOffset.UtcNow - _stepStartedUtc > LifestreamStartGrace)
                TryRestartInterruptedTeleport();

            return;
        }

        if (!CanStartLocalPath())
        {
            DebugCall($"teleport: cannot start local path | {FormatPathGateSummary()}", key: "teleport-no-path");
            return;
        }

        // Still getting up from groundsit — vnav cannot move while InThatPosition.
        if (_condition[ConditionFlag.InThatPosition])
        {
            DebugCall("teleport: waiting for groundsit get-up", key: "teleport-sit");
            return;
        }

        // Leftover SimpleMove pathfind (e.g. from Lifestream) makes MoveTo return false until it clears.
        if (IsVnavPathfindPending())
        {
            DebugCall("teleport: waiting for vnav pathfind", key: "teleport-vnav-pending");
            return;
        }

        if (IsVnavPathRunning())
            StopVnavPath();

        // Prefer mount so the default path can be flight; fall back to foot if mount never comes.
        if (!TryEnsureMountedForPath())
        {
            DebugCall("teleport: waiting for mount (or grace)", key: "teleport-mount");
            return;
        }

        _pathStartDeadlineUtc ??= DateTimeOffset.UtcNow + PathStartGrace;

        if (!TryStartLocalPath())
        {
            if (DateTimeOffset.UtcNow >= _pathStartDeadlineUtc)
                EnterFailedPrompt(DescribePathStartFailure());
            else
                DebugCall($"teleport: path start retry | {DescribePathStartFailure()}", key: "teleport-path-retry");
            return;
        }

        DebugCall("teleport: local pathing started", throttle: false);
    }

    private void TickPathing()
    {
        if (_pending is null)
            return;

        if (DateTimeOffset.UtcNow - _stepStartedUtc > TravelStepTimeout)
        {
            StopOurPath();
            EnterFailedPrompt("Pathing timed out.");
            return;
        }

        // Left the correct housing instance mid-path — stop rather than wall-running.
        if (_pending.IsHousingCall)
        {
            if (_pending.HousingIndoor)
            {
                if (!IsInsideOwnerProperty())
                {
                    StopOurPath();
                    EnterFailedPrompt("Left the owner's house during pathing.");
                    return;
                }
            }
            else if (!IsAtHousingWard() || !IsWithinSameInstancePathRange())
            {
                StopOurPath();
                EnterFailedPrompt("Left the owner's housing ward during pathing.");
                return;
            }
        }

        // Non-housing: never keep pathing after leaving the destination territory.
        if (!_pending.IsHousingCall && _clientState.TerritoryType != _pending.TerritoryId)
        {
            StopOurPath();
            EnterFailedPrompt("Left the destination territory during pathing.");
            return;
        }

        var player = _objects.LocalPlayer;
        if (player is null)
            return;

        var dist = Vector3.Distance(player.Position, _pending.Position);
        DebugCall($"pathing: dist={dist:0.0} yalms | {FormatLocalHousingSnapshot()}", key: "pathing-dist");
        if (dist <= CloseRangeYalms + ArrivedSlopYalms)
        {
            StopOurPath();
            _ = FinishAsync(CallResultStatuses.Arrived, "Arrived near owner.");
            return;
        }

        if (IsVnavBusy())
        {
            _pathingSeenBusy = true;
            return;
        }

        // Path not running yet, or player interrupted vnav — keep trying (do not fail the Call).
        if (!_pathingSeenBusy
            && _pathStartDeadlineUtc is not null
            && DateTimeOffset.UtcNow < _pathStartDeadlineUtc)
        {
            TryStartPath(_pending.Position);
            return;
        }

        DebugCall($"pathing: ended at {dist:0.0}y — retrying", key: "pathing-retry");
        _pathingSeenBusy = false;
        _pathStartDeadlineUtc = DateTimeOffset.UtcNow + PathStartGrace;
        if (TryStartPath(_pending.Position))
            return;

        // Remount / re-settle via Teleporting; overall TravelStepTimeout still applies.
        _timer.SetCallTravelPathingPassThrough(false);
        _phase = TravelPhase.Teleporting;
        _pathStartDeadlineUtc = DateTimeOffset.UtcNow + PathStartGrace;
        DebugCall("pathing: retry deferred to Teleporting", key: "pathing-defer");
    }

    private void EnterFailedPrompt(string? detail = null)
    {
        if (_pending is null)
            return;

        DebugCall(
            $"FAILED: {detail ?? "(no detail)"} | {FormatPendingSummary(_pending)} | {FormatPathGateSummary()}",
            throttle: false,
            force: true);

        AbortCallAutomation();
        _timer.SetCallTravelPathingPassThrough(false);
        _timer.SetCallTravelLifestreamDriving(false);
        _timer.SetCallTravelCastingFlyPass(false);
        _timer.SetCallTravelInputMute(false);
        PluginChat.Print(_chat, CallErrorEcho, PluginChat.Yellow);
        ShowPrompt(CallPromptReason.Failed, canAccept: !IsInCombat() && IsTravelReady);
        _phase = TravelPhase.WaitingAccept;
        _ = AckAsync(CallAckStatuses.Busy);
    }

    private void ShowPrompt(CallPromptReason reason, bool canAccept)
    {
        _prompt.Reason = reason;
        _prompt.CanAccept = canAccept;
        _prompt.IsOpen = true;
    }

    private async Task AckAsync(string status)
    {
        if (_pending is null)
            return;

        if (_localDebugCall)
            return;

        await _sendAck(new CallAckPayload
        {
            RequestId = _pending.RequestId,
            From = _config.PairingKey,
            To = _pending.OwnerKey,
            Status = status,
            Message = status,
        }).ConfigureAwait(false);
    }

    private async Task FinishAsync(string status, string message)
    {
        var pending = _pending;
        var localDebug = _localDebugCall;
        CancelInternal(sendResult: false);
        if (pending is null)
            return;

        if (localDebug)
        {
            if (string.Equals(status, CallResultStatuses.Arrived, StringComparison.Ordinal))
                PluginChat.Print(_chat, "Debug call recall arrived.", PluginChat.Green);
            else if (string.Equals(status, CallResultStatuses.Cancelled, StringComparison.Ordinal))
                PluginChat.Print(_chat, "Debug call recall cancelled.", PluginChat.Grey);
            else
                PluginChat.Print(_chat, CallErrorEcho, PluginChat.Yellow);
            return;
        }

        DebugCall($"result {status}: {message}", throttle: false);

        await _sendResult(new CallResultPayload
        {
            RequestId = pending.RequestId,
            From = _config.PairingKey,
            To = pending.OwnerKey,
            Status = status,
            Message = message,
        }).ConfigureAwait(false);
    }

    private async Task SendResultOrLocalAsync(CallPayload payload, string status, string message, bool localDebug)
    {
        if (localDebug)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            return;
        }

        await _sendResult(new CallResultPayload
        {
            RequestId = payload.RequestId,
            From = _config.PairingKey,
            To = PairingKeyUtil.Normalize(payload.From),
            Status = status,
            Message = message,
        }).ConfigureAwait(false);
    }

    private void CancelInternal(bool sendResult)
    {
        AbortCallAutomation();
        _phase = TravelPhase.Idle;
        _prompt.IsOpen = false;
        _acceptRequested = false;
        _timer.SetCallTravelBypass(false);
        _timer.SetCallTravelPathingPassThrough(false);
        _timer.SetCallTravelLifestreamDriving(false);
        _timer.SetCallTravelCastingFlyPass(false);
        _timer.SetCallTravelInputMute(false);
        _pending = null;
        _localDebugCall = false;
        _ = sendResult;
    }

    private static CallPayload CloneCallPayload(CallPayload source) =>
        new()
        {
            RequestId = source.RequestId,
            From = source.From,
            To = source.To,
            WorldId = source.WorldId,
            WorldName = source.WorldName,
            TerritoryId = source.TerritoryId,
            X = source.X,
            Y = source.Y,
            Z = source.Z,
            HousingCity = source.HousingCity,
            HousingWard = source.HousingWard,
            HousingDivision = source.HousingDivision,
            HousingPlot = source.HousingPlot,
            HousingApartment = source.HousingApartment,
            HousingIsApartment = source.HousingIsApartment,
            HousingIndoor = source.HousingIndoor,
        };

    private static string FormatDebugStoredSummary(CallPayload stored)
    {
        if (stored.HousingWard > 0 && stored.HousingCity != 0 && stored.HousingDivision is 1 or 2)
        {
            var div = stored.HousingDivision == 2 ? "subdivision" : "main";
            var extra = stored.HousingIsApartment
                ? (stored.HousingApartment > 0 ? $", apt {stored.HousingApartment}" : ", apartment")
                : (stored.HousingPlot > 0 ? $", plot {stored.HousingPlot}" : string.Empty);
            var indoor = stored.HousingIndoor ? ", indoor" : string.Empty;
            return $"Stored call point: ward {stored.HousingWard} ({div}{extra}{indoor}), "
                   + $"pos ({stored.X:F1}, {stored.Y:F1}, {stored.Z:F1}).";
        }

        return $"Stored call point: territory {stored.TerritoryId}, "
               + $"pos ({stored.X:F1}, {stored.Y:F1}, {stored.Z:F1}).";
    }

    /// <summary>Stop vnavmesh and abort Lifestream so a cancelled/failed call cannot block the next one.</summary>
    private void AbortCallAutomation()
    {
        try
        {
            _vnavPathStop?.InvokeAction();
        }
        catch
        {
            // ignore
        }

        try
        {
            _lsAbort?.InvokeAction();
        }
        catch
        {
            // ignore
        }

        _weOwnTravel = false;
    }

    private string ClassifyGateStatus()
    {
        if (IsCrafting())
            return CallAckStatuses.Crafting;
        if (IsInInstance())
            return CallAckStatuses.Instance;
        if (IsInCombat())
            return CallAckStatuses.Combat;
        if (IsExternallyBusy())
            return CallAckStatuses.Busy;
        return CallAckStatuses.Traveling;
    }

    private bool IsCrafting() =>
        _condition[ConditionFlag.Crafting] || _condition[ConditionFlag.ExecutingCraftingAction];

    private bool IsInCombat() => _condition[ConditionFlag.InCombat];

    private bool IsBetweenAreas() =>
        _condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51];

    /// <summary>True while casting aetheryte teleport (action 5) or any cast — Teleport IPC is not IsBusy.</summary>
    private bool IsPlayerCasting() =>
        _condition[ConditionFlag.Casting] || _condition[ConditionFlag.Casting87];

    private bool IsInInstance() =>
        _condition[ConditionFlag.BoundByDuty]
        || _condition[ConditionFlag.BoundByDuty56]
        || _condition[ConditionFlag.BoundByDuty95]
        || IsBetweenAreas();

    private bool IsExternallyBusy()
    {
        if (_weOwnTravel)
            return false;
        return IsLifestreamBusy() || IsVnavBusy();
    }

    private bool SameDataCenter(uint targetWorldId)
    {
        var localWorldId = _objects.LocalPlayer?.CurrentWorld.RowId ?? 0;
        if (localWorldId == 0 || targetWorldId == 0)
            return false;
        if (localWorldId == targetWorldId)
            return true;

        var sheet = _data.GetExcelSheet<World>();
        if (sheet is null)
            return false;
        if (!sheet.TryGetRow(localWorldId, out var local) || !sheet.TryGetRow(targetWorldId, out var target))
            return false;
        return local.DataCenter.RowId == target.DataCenter.RowId;
    }

    private void BindIpc()
    {
        if (_ipcBound)
            return;
        try
        {
            _lsIsBusy = _pi.GetIpcSubscriber<bool>("Lifestream.IsBusy");
            _lsAbort = _pi.GetIpcSubscriber<object>("Lifestream.Abort");
            _lsChangeWorldById = _pi.GetIpcSubscriber<uint, bool>("Lifestream.ChangeWorldById");
            _lsChangeWorld = _pi.GetIpcSubscriber<string, bool>("Lifestream.ChangeWorld");
            _lsTeleport = _pi.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport");
            // Action<T> → Subscriber<T, object> + InvokeAction (Dalamud convention).
            _lsGoToHousing = _pi.GetIpcSubscriber<(string, int, int, int, int, int, int, bool, bool, string), object>(
                "Lifestream.GoToHousingAddress");
            _lsHousingAethernetById = _pi.GetIpcSubscriber<uint, bool>("Lifestream.HousingAethernetTeleportById");
            _vnavReady = _pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
            _vnavMoveCloseTo = _pi.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
            _vnavPathRunning = _pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
            _vnavPathfindInProgress = _pi.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
            _vnavPathStop = _pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
            _ipcBound = true;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Call travel IPC bind failed");
        }

        try
        {
            _lsBuildAddress = _pi
                .GetIpcSubscriber<string, string, string, string, bool, bool, (string, int, int, int, int, int, int, bool, bool, string)>(
                    "Lifestream.BuildAddressBookEntry");
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Lifestream BuildAddressBookEntry IPC unavailable");
            _lsBuildAddress = null;
        }

        try
        {
            _lsGetPlotEntrance = _pi.GetIpcSubscriber<uint, int, Vector3?>("Lifestream.GetPlotEntrance");
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Lifestream GetPlotEntrance IPC unavailable");
            _lsGetPlotEntrance = null;
        }
    }

    private bool ProbeTravelReady()
    {
        BindIpc();
        try
        {
            _ = _lsIsBusy?.InvokeFunc();
            var ready = _vnavReady?.InvokeFunc() ?? false;
            return ready;
        }
        catch (IpcNotReadyError)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool IsLifestreamBusy()
    {
        try
        {
            return _lsIsBusy?.InvokeFunc() ?? false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Open walk/fly RMI only while Lifestream or zone transit is driving the doll.
    /// Casting alone opens fly only — walk during cast cancels Teleport and must stay muted.
    /// Idle Call mute zeroes player axes; vnav uses pathing pass-through instead.
    /// </summary>
    private void SyncLifestreamDrivingRmi()
    {
        var inTransitPhase = _phase is TravelPhase.ChangingWorld or TravelPhase.Teleporting;
        var casting = inTransitPhase && IsPlayerCasting();
        var driving = inTransitPhase && (IsLifestreamBusy() || IsBetweenAreas());
        _timer.SetCallTravelLifestreamDriving(driving);
        _timer.SetCallTravelCastingFlyPass(casting);
    }

    private bool IsVnavBusy()
    {
        try
        {
            var running = _vnavPathRunning?.InvokeFunc() ?? false;
            var finding = _vnavPathfindInProgress?.InvokeFunc() ?? false;
            return running || finding;
        }
        catch
        {
            return false;
        }
    }

    private bool TryChangeWorld(PendingCall call)
    {
        try
        {
            if (_lsChangeWorldById is not null && _lsChangeWorldById.InvokeFunc(call.WorldId))
                return true;
            if (!string.IsNullOrWhiteSpace(call.WorldName)
                && _lsChangeWorld is not null
                && _lsChangeWorld.InvokeFunc(call.WorldName))
                return true;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Lifestream ChangeWorld failed");
        }

        return false;
    }

    private bool TryGoToHousing(PendingCall call)
    {
        var plot = HousingCallLocation.ToLifestreamPlot(
            call.HousingPlot > 0 ? call.HousingPlot : 1,
            call.HousingDivision,
            call.HousingIsApartment);
        var apartment = call.HousingApartment > 0 ? call.HousingApartment : 1;
        var isSub = HousingCallLocation.EffectiveDivision(plot, call.HousingDivision, call.HousingIsApartment) == 2;
        var propertyType = call.HousingIsApartment ? 1 : 0; // House=0, Apartment=1
        var worldName = !string.IsNullOrWhiteSpace(call.WorldName)
            ? call.WorldName
            : ResolveWorldName(call.WorldId);
        var cityName = ResidentialCityName(call.HousingCity);
        var plotOrApt = call.HousingIsApartment ? apartment : plot;

        try
        {
            if (_lsBuildAddress is not null
                && !string.IsNullOrWhiteSpace(worldName)
                && !string.IsNullOrWhiteSpace(cityName))
            {
                var built = _lsBuildAddress.InvokeFunc(
                    worldName,
                    cityName,
                    call.HousingWard.ToString(),
                    plotOrApt.ToString(),
                    call.HousingIsApartment,
                    isSub);
                // BuildAddressBookEntry returns default tuple when parsing fails.
                if (built.Item3 != 0 && built.Item4 != 0)
                {
                    _lsGoToHousing?.InvokeAction(built);
                    return _lsGoToHousing is not null;
                }
            }

            if (_lsGoToHousing is null)
                return false;

            var entry = (
                string.Empty,
                (int)call.WorldId,
                call.HousingCity,
                call.HousingWard,
                propertyType,
                plot,
                apartment,
                isSub,
                false,
                string.Empty);
            _lsGoToHousing.InvokeAction(entry);
            return true;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Lifestream GoToHousingAddress failed");
            return false;
        }
    }

    /// <summary>
    /// Outdoor street calls have no current plot in HousingManager. Use Lifestream's plot entrance
    /// coordinates to select the closest address in the correct division, then vnav the remaining
    /// street distance from there. This avoids treating plot 0 as plot 1, which can land in a
    /// distant part of the ward or even the wrong division.
    /// </summary>
    private int FindNearestHousingPlot(int housingCity, int housingDivision, Vector3 destination)
    {
        if (_lsGetPlotEntrance is null)
        {
            DebugCall("street anchor: GetPlotEntrance IPC unavailable; retaining plot 0", throttle: false);
            return 0;
        }

        var territory = HousingCallLocation.TryGetResidentialTerritoryForCity(housingCity);
        if (territory is null)
        {
            DebugCall($"street anchor: no residential territory for city={housingCity}", throttle: false);
            return 0;
        }

        // Lifestream plot indexes are zero-based across the whole ward:
        // 0–29 main division and 30–59 subdivision.
        var firstPlotIndex = housingDivision == 2 ? 30 : 0;
        var bestPlotIndex = -1;
        var bestDistanceSquared = float.MaxValue;

        for (var plotIndex = firstPlotIndex; plotIndex < firstPlotIndex + 30; plotIndex++)
        {
            try
            {
                var entrance = _lsGetPlotEntrance.InvokeFunc(territory.Value, plotIndex);
                if (entrance is null)
                    continue;

                var dx = entrance.Value.X - destination.X;
                var dz = entrance.Value.Z - destination.Z;
                var distanceSquared = (dx * dx) + (dz * dz);
                if (distanceSquared >= bestDistanceSquared)
                    continue;

                bestDistanceSquared = distanceSquared;
                bestPlotIndex = plotIndex;
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "GetPlotEntrance failed while resolving nearest street anchor");
            }
        }

        if (bestPlotIndex < 0)
        {
            DebugCall(
                $"street anchor: no plot entrances found terr={territory.Value} div={housingDivision}",
                throttle: false);
            return 0;
        }

        var plot = bestPlotIndex + 1;
        DebugCall(
            $"street anchor: nearest plot={plot} div={housingDivision} "
            + $"distance={MathF.Sqrt(bestDistanceSquared):0.0}y "
            + $"dest=({destination.X:0.0},{destination.Y:0.0},{destination.Z:0.0})",
            throttle: false);
        return plot;
    }

    /// <summary>
    /// When GoToHousingAddress no-ops, teleport to the residential city aetheryte then retry once.
    /// <see cref="PendingCall.HousingCity"/> is Lifestream's ResidentialAetheryteKind (city aetheryte row id).
    /// </summary>
    private bool TryRecoverStalledHousingTravel()
    {
        if (_pending is null || !_pending.IsHousingCall)
            return false;

        if (!_housingCityTeleportAttempted && _pending.HousingCity > 0)
        {
            _housingCityTeleportAttempted = true;
            try
            {
                if (_lsTeleport?.InvokeFunc((uint)_pending.HousingCity, 0) == true)
                {
                    _stepStartedUtc = DateTimeOffset.UtcNow;
                    PluginChat.Print(_chat, "Retrying via city aetheryte…", PluginChat.Yellow);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Lifestream city aetheryte teleport failed");
            }
        }

        if (_housingGoRetryAttempted)
            return false;

        _housingGoRetryAttempted = true;
        if (TryGoToHousing(_pending))
        {
            _stepStartedUtc = DateTimeOffset.UtcNow;
            PluginChat.Print(_chat, "Retrying housing travel…", PluginChat.Yellow);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Re-issue Teleport / GoToHousing after an interrupted cast or cancelled Lifestream task.
    /// Rate-limited; does not fail the Call — overall TravelStepTimeout still applies.
    /// </summary>
    private bool TryRestartInterruptedTeleport()
    {
        if (_pending is null)
            return false;
        if (IsBetweenAreas() || IsLifestreamBusy() || IsPlayerCasting())
            return false;
        if (DateTimeOffset.UtcNow - _lastTeleportRetryUtc < TeleportRetryCooldown)
            return false;

        _lastTeleportRetryUtc = DateTimeOffset.UtcNow;

        if (_pending.IsHousingCall)
        {
            // Prefer the one-shot city / GoToHousing recovery first, then unrestricted retries.
            if (TryRecoverStalledHousingTravel())
            {
                DebugCall("teleport: recover stalled housing", key: "teleport-recover");
                return true;
            }

            if (TryGoToHousing(_pending))
            {
                _teleportProgressSeen = false;
                DebugCall("teleport: re-issued GoToHousingAddress", key: "teleport-retry-housing");
                return true;
            }

            return false;
        }

        if (_clientState.TerritoryType == _pending.TerritoryId)
            return false;

        if (TryTeleportNear(_pending.TerritoryId, _pending.Position))
        {
            _teleportProgressSeen = false;
            DebugCall("teleport: re-issued aetheryte Teleport", key: "teleport-retry");
            return true;
        }

        return false;
    }

    private static string ResidentialCityName(int city) =>
        city switch
        {
            HousingCallLocation.CityLimsa => "Mist",
            HousingCallLocation.CityGridania => "Lavender Beds",
            HousingCallLocation.CityUldah => "Goblet",
            HousingCallLocation.CityFoundation => "Empyreum",
            HousingCallLocation.CityKugane => "Shirogane",
            _ => string.Empty,
        };

    private string ResolveWorldName(uint worldId)
    {
        if (worldId == 0)
            return string.Empty;
        try
        {
            if (_data.GetExcelSheet<World>()?.TryGetRow(worldId, out var row) == true)
                return row.Name.ToString();
        }
        catch
        {
            // ignore
        }

        return string.Empty;
    }

    private bool TryTeleportNear(uint territoryId, Vector3 dest)
    {
        if (!TryFindNearestAetheryte(territoryId, dest, out var aetheryteId))
            return false;

        try
        {
            return _lsTeleport?.InvokeFunc(aetheryteId, 0) ?? false;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Lifestream Teleport failed");
            return false;
        }
    }

    private bool TryStartPath(Vector3 dest)
    {
        try
        {
            if (_vnavMoveCloseTo is null)
                return false;

            // Nav mesh rebuilds after zone change — wait rather than fail the Call.
            if (_vnavReady?.InvokeFunc() != true)
                return false;

            // MoveTo returns false while a SimpleMove pathfind is already queued — wait it out.
            if (IsVnavPathfindPending())
                return false;

            // Default: flight when mounted / airborne, except short final legs where takeoff is
            // slower and can fail silently near housing-lot boundaries.
            if (!ShouldPreferWalking()
                && !_pending!.HousingIndoor
                && (IsMounted()
                    || _condition[ConditionFlag.InFlight]
                    || _condition[ConditionFlag.Diving]))
            {
                if (_vnavMoveCloseTo.InvokeFunc(dest, true, CloseRangeYalms))
                    return true;

                // Fly queue rejected (usually busy) — wait; do not immediately queue a ground path
                // on top of the same pending task.
                if (IsVnavPathfindPending())
                    return false;
            }

            return _vnavMoveCloseTo.InvokeFunc(dest, false, CloseRangeYalms);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "vnavmesh PathfindAndMoveCloseTo failed");
            return false;
        }
    }

    /// <summary>Start vnav only after we are in the destination instance.</summary>
    private bool TryStartLocalPath()
    {
        if (_pending is null || !CanStartLocalPath())
            return false;

        // Enable pathing tweaks before starting vnav (Jump allow + IgnoreUserInput).
        _timer.SetCallTravelPathingPassThrough(true);
        _timer.SetCallTravelMuteDestination(_pending.Position);

        if (!TryStartPath(_pending.Position))
        {
            _timer.SetCallTravelPathingPassThrough(false);
            return false;
        }

        _weOwnTravel = true;
        _pathingSeenBusy = false;
        _pathStartDeadlineUtc = DateTimeOffset.UtcNow + PathStartGrace;
        _phase = TravelPhase.Pathing;
        _stepStartedUtc = DateTimeOffset.UtcNow;
        return true;
    }

    private string DescribePathStartFailure()
    {
        try
        {
            if (_vnavMoveCloseTo is null)
                return "Could not start pathing near the owner (vnavmesh IPC missing).";
            if (_vnavReady?.InvokeFunc() != true)
                return "Could not start pathing near the owner (vnavmesh not ready).";
            if (IsVnavPathfindPending())
                return "Could not start pathing near the owner (vnavmesh still pathfinding).";
        }
        catch
        {
            // fall through
        }

        return "Could not start pathing near the owner (no path).";
    }

    private bool CanStartLocalPath()
    {
        if (_pending is null)
            return false;
        if (!IsAtCallDestination())
            return false;

        if (_pending.IsHousingCall)
        {
            // Indoor: arrival is "inside the house" — never start vnav through interiors.
            if (_pending.HousingIndoor)
                return false;

            // Housing main/sub share a TerritoryType but sit ~700 yalms apart — never path across that.
            return IsWithinSameInstancePathRange();
        }

        return true;
    }

    /// <summary>
    /// Prefer Mount Roulette so Call can use a flight path by default. Returns true when mounted,
    /// indoor/close, or mount is unavailable / grace expired (foot fallback). Returns false while
    /// still waiting on a mount attempt.
    /// </summary>
    private unsafe bool TryEnsureMountedForPath()
    {
        if (_pending is null)
            return false;

        // Short final legs are faster and more reliable on foot. If Lifestream already left us
        // mounted, TryStartPath still explicitly requests a ground path.
        var player = _objects.LocalPlayer;
        if (player is not null
            && Vector3.Distance(player.Position, _pending.Position) <= PreferWalkingWithinYalms)
            return true;

        if (_pending.HousingIndoor)
            return true;

        if (IsMounted() || _condition[ConditionFlag.InFlight] || _condition[ConditionFlag.Diving])
            return true;

        _mountDeadlineUtc ??= DateTimeOffset.UtcNow + MountGrace;

        var now = DateTimeOffset.UtcNow;
        // Mount never came — fall back to foot pathing.
        if (now >= _mountDeadlineUtc.Value)
            return true;

        if (_condition[ConditionFlag.Mounting]
            || _condition[ConditionFlag.Mounting71]
            || _condition[ConditionFlag.MountOrOrnamentTransition]
            || IsPlayerCasting()
            || IsBetweenAreas())
            return false;

        if (now - _lastMountAttemptUtc < MountAttemptCooldown)
            return false;

        try
        {
            var am = ActionManager.Instance();
            if (am is null)
                return true;

            // 0 = ready. Non-zero: on cooldown, in combat, or unavailable — keep waiting until grace.
            if (am->GetActionStatus(ActionType.GeneralAction, GeneralActionMountRoulette) != 0)
                return false;

            _lastMountAttemptUtc = now;
            am->UseAction(ActionType.GeneralAction, GeneralActionMountRoulette);
            return false;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Mount Roulette failed");
            return true;
        }
    }

    private bool IsMounted() => _condition[ConditionFlag.Mounted];

    private bool ShouldPreferWalking()
    {
        var player = _objects.LocalPlayer;
        return player is not null
               && _pending is not null
               && Vector3.Distance(player.Position, _pending.Position) <= PreferWalkingWithinYalms;
    }

    private bool IsVnavPathfindPending()
    {
        try
        {
            return _vnavPathfindInProgress?.InvokeFunc() ?? false;
        }
        catch
        {
            return false;
        }
    }

    private bool IsVnavPathRunning()
    {
        try
        {
            return _vnavPathRunning?.InvokeFunc() ?? false;
        }
        catch
        {
            return false;
        }
    }

    private void StopVnavPath()
    {
        try
        {
            _vnavPathStop?.InvokeAction();
        }
        catch
        {
            // ignore
        }
    }

    private bool IsWithinSameInstancePathRange()
    {
        if (_pending is null)
            return false;
        var player = _objects.LocalPlayer;
        if (player is null)
            return false;

        var dx = player.Position.X - _pending.Position.X;
        var dz = player.Position.Z - _pending.Position.Z;
        var distSq = dx * dx + dz * dz;
        return distSq <= MaxSameInstancePathYalms * MaxSameInstancePathYalms;
    }

    private void StopOurPath()
    {
        if (!_weOwnTravel)
            return;
        try
        {
            _vnavPathStop?.InvokeAction();
        }
        catch
        {
            // ignore
        }
    }

    private bool IsAtCallDestination()
    {
        if (_pending is null)
            return false;
        if (_pending.IsHousingCall)
        {
            if (_pending.HousingIndoor)
                return IsInsideOwnerProperty();
            return IsAtHousingWard();
        }

        return _clientState.TerritoryType == _pending.TerritoryId;
    }

    /// <summary>
    /// True when in the owner's housing district: outdoor ward/division, or inside a house whose
    /// district matches (indoor territories are not the Mist/Goblet ids).
    /// </summary>
    private unsafe bool IsAtHousingWard()
    {
        if (_pending is null || !_pending.IsHousingCall)
            return false;

        var expectedTerritory = HousingCallLocation.TryGetResidentialTerritoryForCity(_pending.HousingCity)
                                ?? _pending.TerritoryId;

        var h = HousingManager.Instance();
        if (h is null)
            return false;

        if (h->IsInside())
        {
            // Prefer HousingCallLocation.TryRead — GetOriginalHouseTerritoryTypeId can be 0.
            if (HousingCallLocation.TryRead(_clientState.TerritoryType, territoryRow: null, out var insideLoc, _data)
                && insideLoc.Indoor)
            {
                return insideLoc.City == _pending.HousingCity
                       && insideLoc.Ward == _pending.HousingWard
                       && insideLoc.Division == HousingCallLocation.EffectiveDivision(
                           _pending.HousingPlot,
                           _pending.HousingDivision,
                           _pending.HousingIsApartment);
            }

            var original = HousingManager.GetOriginalHouseTerritoryTypeId();
            if (original == 0 || original != expectedTerritory)
                return false;

            return MatchesPendingHousingWardDivision(h);
        }

        if (_clientState.TerritoryType != expectedTerritory)
            return false;

        return MatchesPendingHousingWardDivision(h);
    }

    /// <summary>True when inside the owner's private house or apartment (plot/room match).</summary>
    private unsafe bool IsInsideOwnerProperty()
    {
        if (_pending is null || !_pending.IsHousingCall)
            return false;

        if (HousingCallLocation.TryRead(_clientState.TerritoryType, territoryRow: null, out var loc, _data)
            && loc.Indoor)
        {
            if (loc.City != _pending.HousingCity
                || loc.Ward != _pending.HousingWard
                || loc.Division != _pending.HousingDivision)
                return false;

            if (_pending.HousingIsApartment)
            {
                if (!loc.IsApartment)
                    return false;
                if (_pending.HousingApartment > 0
                    && loc.Apartment > 0
                    && loc.Apartment != _pending.HousingApartment)
                    return false;
                return true;
            }

            if (loc.IsApartment)
                return false;
            if (_pending.HousingPlot > 0 && loc.Plot > 0)
            {
                var pendingPlot = HousingCallLocation.ToLifestreamPlot(
                    _pending.HousingPlot,
                    _pending.HousingDivision,
                    false);
                var localPlot = HousingCallLocation.ToLifestreamPlot(loc.Plot, loc.Division, false);
                if (pendingPlot != localPlot)
                    return false;
            }

            return true;
        }

        var h = HousingManager.Instance();
        if (h is null || !h->IsInside())
            return false;

        if (!IsAtHousingWard())
            return false;

        var rawPlot = h->GetCurrentPlot();
        if (_pending.HousingIsApartment)
        {
            if (rawPlot is not (-128 or -127))
                return false;
            var room = h->GetCurrentRoom();
            if (_pending.HousingApartment > 0 && room > 0 && room != _pending.HousingApartment)
                return false;
            return true;
        }

        if (rawPlot < 0)
            return false;

        var plot = rawPlot + 1;
        var pendingLs = HousingCallLocation.ToLifestreamPlot(
            _pending.HousingPlot,
            _pending.HousingDivision,
            false);
        if (_pending.HousingPlot > 0 && plot != pendingLs)
            return false;

        return true;
    }

    private unsafe bool MatchesPendingHousingWardDivision(HousingManager* h)
    {
        if (_pending is null)
            return false;

        var ward = h->GetCurrentWard() + 1;
        if (ward != _pending.HousingWard)
            return false;

        var division = h->GetCurrentDivision();
        var rawPlot = h->GetCurrentPlot();
        if (rawPlot == -127)
            division = 2;
        else if (rawPlot == -128)
            division = 1;
        else if (rawPlot >= 30)
            division = 2;
        else if (rawPlot is >= 0 and < 30 && division is not (1 or 2))
            division = 1;

        var expected = HousingCallLocation.EffectiveDivision(
            _pending.HousingPlot,
            _pending.HousingDivision,
            _pending.HousingIsApartment);
        return division == expected;
    }

    /// <summary>
    /// Lifestream GoToHousingAddress stops at the plot door. Interact + confirm to go indoors.
    /// </summary>
    private unsafe void TryProgressIndoorHouseEntry()
    {
        if (_pending is null)
            return;

        if (_pending.HousingIsApartment)
        {
            if (TryConfirmHousingEntranceYesno(apartment: true, out var apartmentYesnoDetail))
            {
                _teleportProgressSeen = true;
                DebugCall(
                    $"enter-apartment: confirmed SelectYesno ({apartmentYesnoDetail})",
                    key: "enter-apartment-yesno");
            }

            // Lifestream owns selecting and interacting with the apartment entrance.
            return;
        }

        // Always prefer confirming an open estate-hall prompt (do not re-interact over it).
        if (TryConfirmHousingEntranceYesno(apartment: false, out var yesnoDetail))
        {
            _awaitingHouseEnterConfirm = false;
            _teleportProgressSeen = true;
            DebugCall($"enter-house: confirmed SelectYesno ({yesnoDetail})", key: "enter-yesno");
            return;
        }

        if (_awaitingHouseEnterConfirm)
        {
            var waited = DateTimeOffset.UtcNow - _houseEnterInteractedUtc;
            if (waited < TimeSpan.FromSeconds(4))
            {
                DebugCall(
                    $"enter-house: waiting for SelectYesno ({yesnoDetail})",
                    key: "enter-wait-yesno");
                return;
            }

            // Dialog never matched / never appeared — allow another interact.
            _awaitingHouseEnterConfirm = false;
            DebugCall($"enter-house: SelectYesno wait timed out ({yesnoDetail})", throttle: false);
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastHouseEnterAttemptUtc < HouseEnterAttemptCooldown)
            return;
        _lastHouseEnterAttemptUtc = now;

        var player = _objects.LocalPlayer;
        if (player is null)
            return;

        if (_condition[ConditionFlag.Mounted]
            || _condition[ConditionFlag.InFlight]
            || _condition[ConditionFlag.Mounting]
            || _condition[ConditionFlag.MountOrOrnamentTransition])
        {
            DebugCall("enter-house: waiting to dismount", key: "enter-dismount");
            return;
        }

        var entrance = FindNearestHouseEntrance(out var dist);
        if (entrance is not null && dist <= HouseEntranceInteractYalms)
        {
            if (IsVnavPathRunning())
                StopVnavPath();
            // Door approach done — block player walk again while interacting / confirming.
            _timer.SetCallTravelPathingPassThrough(false);

            if (_targets.Target?.GameObjectId != entrance.GameObjectId)
            {
                _targets.Target = entrance;
                DebugCall("enter-house: targeting Entrance", key: "enter-target");
            }

            try
            {
                var ts = TargetSystem.Instance();
                if (ts is null)
                    return;
                ts->InteractWithObject((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)entrance.Address, false);
                _teleportProgressSeen = true;
                _awaitingHouseEnterConfirm = true;
                _houseEnterInteractedUtc = now;
                DebugCall("enter-house: interacted with Entrance", key: "enter-interact");
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "House entrance interact failed");
            }

            return;
        }

        if (IsVnavPathRunning())
        {
            DebugCall(
                $"enter-house: approaching door (entrance={(entrance is null ? "none" : $"{dist:0.0}y")})",
                key: "enter-approach");
            return;
        }

        TryPathTowardPlotEntrance(player.Position, entrance, dist);
        DebugCall(
            $"enter-house: seeking door (entrance={(entrance is null ? "none" : $"{dist:0.0}y")})",
            key: "enter-seek");
    }

    private IGameObject? FindNearestHouseEntrance(out float distance)
    {
        distance = float.MaxValue;
        IGameObject? best = null;
        var player = _objects.LocalPlayer;
        if (player is null)
            return null;

        foreach (var obj in _objects)
        {
            if (obj is null || !obj.IsTargetable)
                continue;
            var name = obj.Name.TextValue;
            if (string.IsNullOrEmpty(name))
                continue;
            if (!IsHouseEntranceName(name))
                continue;

            var d = Vector3.Distance(player.Position, obj.Position);
            if (d < distance)
            {
                distance = d;
                best = obj;
            }
        }

        return best;
    }

    private static bool IsHouseEntranceName(string name)
    {
        foreach (var n in HouseEntranceNames)
        {
            if (name.Equals(n, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Confirm a housing entrance SelectYesno. Prompt text lives on node 15 (same as Lifestream),
    /// not in AtkValues. Apartment calls may accept the visible dialog after Lifestream has already
    /// selected the exact room; private-house calls still require an entrance-text match.
    /// </summary>
    private unsafe bool TryConfirmHousingEntranceYesno(bool apartment, out string detail)
    {
        detail = "no addon";
        try
        {
            // Scan a few SelectYesno instances (index 1+), matching Lifestream.
            for (var index = 1; index <= 8; index++)
            {
                var addonPtr = _gameGui.GetAddonByName("SelectYesno", index);
                if (addonPtr.IsNull || !addonPtr.IsReady || !addonPtr.IsVisible)
                    continue;

                var addon = (AtkUnitBase*)addonPtr.Address;
                if (addon is null)
                    continue;

                var prompt = ReadSelectYesnoPrompt(addon);
                detail = string.IsNullOrEmpty(prompt)
                    ? $"addon#{index} empty prompt"
                    : $"addon#{index} '{TruncateForDebug(prompt, 80)}'";

                if (string.IsNullOrEmpty(prompt))
                {
                    // Apartment selection is already exact and owned by Lifestream. For houses,
                    // accept an empty prompt only after our own entrance interaction.
                    if (!apartment && !_awaitingHouseEnterConfirm)
                        continue;
                }
                else
                {
                    var normalized = prompt.Replace(" ", "", StringComparison.Ordinal);
                    // At this point an apartment Call is in the correct ward and Lifestream is
                    // waiting after selecting the requested apartment, so its visible Yes/No is safe.
                    var matched = apartment;
                    foreach (var fragment in HouseEnterConfirmText)
                    {
                        var needle = fragment.Replace(" ", "", StringComparison.Ordinal);
                        if (normalized.Contains(needle, StringComparison.OrdinalIgnoreCase))
                        {
                            matched = true;
                            break;
                        }
                    }

                    // Loose fallback: estate / house / ハウス / Gebäude etc.
                    if (!matched
                        && (normalized.Contains("estate", StringComparison.OrdinalIgnoreCase)
                            || normalized.Contains("ハウス", StringComparison.Ordinal)
                            || normalized.Contains("房屋", StringComparison.Ordinal)
                            || normalized.Contains("Gebäude", StringComparison.OrdinalIgnoreCase)
                            || normalized.Contains("maison", StringComparison.OrdinalIgnoreCase)
                            || normalized.Contains("주택", StringComparison.Ordinal)))
                    {
                        matched = true;
                    }

                    if (!matched && !_awaitingHouseEnterConfirm)
                        continue;
                }

                addon->FireCallbackInt(0); // Yes
                detail += " → Yes";
                return true;
            }

            if (detail == "no addon" && _awaitingHouseEnterConfirm)
                detail = "waiting (SelectYesno not visible)";
            return false;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "House entrance SelectYesno failed");
            detail = $"exception: {ex.Message}";
            return false;
        }
    }

    private static unsafe string ReadSelectYesnoPrompt(AtkUnitBase* addon)
    {
        try
        {
            // Lifestream: UldManager.NodeList[15] text node holds the prompt.
            if (addon->UldManager.NodeListCount > 15)
            {
                var node = addon->UldManager.NodeList[15];
                if (node is not null)
                {
                    var textNode = node->GetAsAtkTextNode();
                    if (textNode is not null)
                    {
                        var fromGetText = textNode->GetText();
                        if (fromGetText.HasValue)
                        {
                            var s = fromGetText.ToString();
                            if (!string.IsNullOrWhiteSpace(s))
                                return s;
                        }

                        var fromNodeText = textNode->NodeText.ToString();
                        if (!string.IsNullOrWhiteSpace(fromNodeText))
                            return fromNodeText;
                    }
                }
            }

            // Fallback: AtkValues string payloads.
            for (var i = 0; i < addon->AtkValuesCount; i++)
            {
                ref var value = ref addon->AtkValues[i];
                try
                {
                    if (!value.String.HasValue)
                        continue;
                    var s = value.String.ToString();
                    if (!string.IsNullOrWhiteSpace(s) && s.Length > 3)
                        return s;
                }
                catch
                {
                    // ignore bad value types
                }
            }
        }
        catch
        {
            // ignore
        }

        return string.Empty;
    }

    private static string TruncateForDebug(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// Walk to the plot door. Prefer a visible Entrance object; else Lifestream GetPlotEntrance.
    /// </summary>
    private void TryPathTowardPlotEntrance(Vector3 playerPos, IGameObject? visibleEntrance, float visibleDist)
    {
        if (_pending is null)
            return;
        if (IsLifestreamBusy())
        {
            DebugCall("enter-house: path wait (lifestream busy)", key: "enter-path-wait");
            return;
        }

        // Allow restart if a previous attempt ended short of the door.
        if (_houseEntrancePathStarted && IsVnavBusy())
            return;
        _houseEntrancePathStarted = false;

        Vector3? dest = null;
        string destSource;

        if (visibleEntrance is not null
            && visibleDist is > 0 and <= HouseEntranceApproachMaxYalms)
        {
            dest = visibleEntrance.Position;
            destSource = $"Entrance obj ({visibleDist:0.0}y)";
        }
        else
        {
            try
            {
                var plotIndex = Math.Max(0, HousingCallLocation.ToLifestreamPlot(
                    _pending.HousingPlot,
                    _pending.HousingDivision,
                    false) - 1);
                var territory = HousingCallLocation.TryGetResidentialTerritoryForCity(_pending.HousingCity)
                                ?? _pending.TerritoryId;
                dest = _lsGetPlotEntrance?.InvokeFunc(territory, plotIndex);
                destSource = dest is null
                    ? $"GetPlotEntrance null (terr={territory} plotIdx={plotIndex})"
                    : $"GetPlotEntrance plotIdx={plotIndex}";
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "GetPlotEntrance failed");
                destSource = "GetPlotEntrance threw";
            }
        }

        if (dest is null)
        {
            DebugCall($"enter-house: no door dest ({destSource})", key: "enter-no-dest");
            return;
        }

        var dist = Vector3.Distance(playerPos, dest.Value);
        if (dist <= HouseEntranceInteractYalms)
            return;

        try
        {
            if (_vnavReady?.InvokeFunc() != true)
            {
                DebugCall("enter-house: vnav not ready", key: "enter-vnav-ready");
                return;
            }

            if (_vnavMoveCloseTo is null)
            {
                DebugCall("enter-house: vnav MoveCloseTo missing", key: "enter-vnav-missing");
                return;
            }

            // Cancel leftover pathfind so MoveCloseTo can start.
            if (IsVnavPathfindPending() || IsVnavPathRunning())
                StopVnavPath();

            if (_vnavMoveCloseTo.InvokeFunc(dest.Value, false, HouseEntranceInteractYalms))
            {
                _houseEntrancePathStarted = true;
                _weOwnTravel = true;
                _teleportProgressSeen = true;
                _timer.SetCallTravelPathingPassThrough(true);
                DebugCall(
                    $"enter-house: pathing to door via {destSource} ({dist:0.0}y) → ({dest.Value.X:0.0},{dest.Value.Y:0.0},{dest.Value.Z:0.0})",
                    throttle: false);
            }
            else
            {
                DebugCall($"enter-house: MoveCloseTo returned false ({destSource}, {dist:0.0}y)", key: "enter-vnav-false");
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Path to plot entrance failed");
            DebugCall($"enter-house: path exception: {ex.Message}", key: "enter-path-ex");
        }
    }

    private string PresenceRequiredEcho()
    {
        if (_pending is null)
            return "Your presence is required by your owner.";

        var pair = _config.FindPairByKey(_pending.OwnerKey);
        var label = pair?.GetMessageLabel();
        if (string.IsNullOrWhiteSpace(label) || string.Equals(label, "DEBUGCALL", StringComparison.Ordinal))
            label = "your owner";

        return $"Your presence is required by {label}.";
    }

    private void ReportEdgeCaseError(string detail)
    {
        PluginChat.Print(_chat, CallErrorEcho, PluginChat.Yellow);
        DebugCall($"ERROR: {detail}", throttle: false, force: true);
    }

    /// <summary>
    /// Append to config-dir CallTravel.debug.log. Routine diagnostics require debug mode;
    /// error details are forced so the generic player-facing echo always has a useful report.
    /// </summary>
    private void DebugCall(string message, string? key = null, bool throttle = true, bool force = false)
    {
        if (!force && !_config.IsDebugEnabled)
            return;

        if (throttle)
        {
            var throttleKey = key ?? message;
            var now = DateTimeOffset.UtcNow;
            if (string.Equals(throttleKey, _lastCallDebugKey, StringComparison.Ordinal)
                && now - _lastCallDebugUtc < TimeSpan.FromSeconds(2))
                return;
            _lastCallDebugKey = throttleKey;
            _lastCallDebugUtc = now;
        }

        CallTravelDebugLog.Write(_pi, _config, _log, message, force);
    }

    private static string FormatPendingSummary(PendingCall call)
    {
        var pos = call.Position;
        if (!call.IsHousingCall)
            return $"terr={call.TerritoryId} pos=({pos.X:0.0},{pos.Y:0.0},{pos.Z:0.0}) world={call.WorldId}";

        var kind = call.HousingIsApartment ? "apt" : "house";
        var indoor = call.HousingIndoor ? "indoor" : "outdoor";
        return $"housing {indoor} {kind} city={call.HousingCity} w{call.HousingWard} "
               + $"div{call.HousingDivision} plot={call.HousingPlot} apt={call.HousingApartment} "
               + $"terr={call.TerritoryId} pos=({pos.X:0.0},{pos.Y:0.0},{pos.Z:0.0})";
    }

    private string FormatPathGateSummary()
    {
        var player = _objects.LocalPlayer;
        var dist = player is null || _pending is null
            ? -1f
            : Vector3.Distance(player.Position, _pending.Position);
        return $"phase={_phase} terr={_clientState.TerritoryType} dist={dist:0.0} "
               + $"atDest={IsAtCallDestination()} atWard={IsAtHousingWard()} "
               + $"insideProp={IsInsideOwnerProperty()} inRange={IsWithinSameInstancePathRange()} "
               + $"canPath={CanStartLocalPath()} | {FormatLocalHousingSnapshot()}";
    }

    private unsafe string FormatLocalHousingSnapshot()
    {
        var player = _objects.LocalPlayer;
        var pos = player?.Position ?? default;
        var h = HousingManager.Instance();
        if (h is null)
            return $"local pos=({pos.X:0.0},{pos.Y:0.0},{pos.Z:0.0}) housing=null";

        try
        {
            var ward = h->GetCurrentWard();
            var div = h->GetCurrentDivision();
            var plot = h->GetCurrentPlot();
            var room = h->GetCurrentRoom();
            var inside = h->IsInside();
            var original = HousingManager.GetOriginalHouseTerritoryTypeId();
            var read = HousingCallLocation.TryRead(_clientState.TerritoryType, null, out var loc, _data)
                ? $"read city={loc.City} w{loc.Ward} div{loc.Division} plot={loc.Plot} apt={loc.Apartment} "
                  + $"aptFlag={loc.IsApartment} indoor={loc.Indoor} outTerr={loc.OutdoorTerritoryId}"
                : "read=fail";
            return $"local pos=({pos.X:0.0},{pos.Y:0.0},{pos.Z:0.0}) "
                   + $"hm wardIdx={ward} div={div} plot={plot} room={room} inside={inside} origTerr={original} | {read}";
        }
        catch (Exception ex)
        {
            return $"local housing snapshot error: {ex.Message}";
        }
    }

    private unsafe bool TryHopToSubdivisionIfNeeded()
    {
        if (_pending is null || _subdivisionHopAttempted || !_pending.IsHousingCall)
            return false;
        if (_pending.HousingDivision != 2
            && HousingCallLocation.EffectiveDivision(
                _pending.HousingPlot,
                _pending.HousingDivision,
                _pending.HousingIsApartment) != 2)
            return false;

        var h = HousingManager.Instance();
        if (h is null)
            return false;

        var ward = h->GetCurrentWard() + 1;
        if (ward != _pending.HousingWard)
            return false;
        if (h->GetCurrentDivision() == 2)
            return false;

        if (!SubdivisionAetheryteByCity.TryGetValue(_pending.HousingCity, out var aetheryteId))
            return false;

        _subdivisionHopAttempted = true;
        try
        {
            if (_lsHousingAethernetById?.InvokeFunc(aetheryteId) == true)
            {
                _stepStartedUtc = DateTimeOffset.UtcNow;
                return true;
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Lifestream HousingAethernetTeleportById failed");
        }

        return false;
    }

    private bool TryFindNearestAetheryte(uint territoryId, Vector3 dest, out uint aetheryteId)
    {
        aetheryteId = 0;
        var sheet = _data.GetExcelSheet<Aetheryte>();
        if (sheet is null)
            return false;

        var bestDist = float.MaxValue;
        uint bestId = 0;
        var any = false;

        foreach (var row in sheet)
        {
            if (!row.IsAetheryte)
                continue;
            if (row.Territory.RowId != territoryId)
                continue;

            any = true;
            var pos = TryGetAetheryteWorldPos(row);
            if (pos is null)
            {
                if (bestId == 0)
                    bestId = row.RowId;
                continue;
            }

            var d = Vector2.Distance(new Vector2(dest.X, dest.Z), new Vector2(pos.Value.X, pos.Value.Y));
            if (d < bestDist)
            {
                bestDist = d;
                bestId = row.RowId;
            }
        }

        if (bestId == 0 && any)
        {
            foreach (var row in sheet)
            {
                if (!row.IsAetheryte || row.Territory.RowId != territoryId)
                    continue;
                bestId = row.RowId;
                break;
            }
        }

        aetheryteId = bestId;
        return aetheryteId != 0;
    }

    private Vector2? TryGetAetheryteWorldPos(Aetheryte row)
    {
        try
        {
            // Map markers with DataType 3 are aetherytes; DataKey is the aetheryte row id.
            var markers = _data.GetSubrowExcelSheet<MapMarker>();
            if (markers is null)
                return null;

            foreach (var subrow in markers.Flatten())
            {
                if (subrow.DataType != 3 || subrow.DataKey.RowId != row.RowId)
                    continue;

                var mapSheet = _data.GetExcelSheet<Map>();
                if (mapSheet is null || !mapSheet.TryGetRow(subrow.RowId, out var map))
                {
                    // Subrow sheet RowId is the Map row for some Lumina versions; fall back via territory.
                    map = default;
                    var found = false;
                    if (mapSheet is not null)
                    {
                        foreach (var m in mapSheet)
                        {
                            if (m.TerritoryType.RowId != row.Territory.RowId)
                                continue;
                            map = m;
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                        return ConvertMarker(subrow.X, subrow.Y, 100);
                }

                var scale = map.SizeFactor == 0 ? 100f : map.SizeFactor;
                return ConvertMarker(subrow.X, subrow.Y, scale);
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Aetheryte map marker lookup failed");
        }

        return null;
    }

    private static Vector2 ConvertMarker(int markerX, int markerY, float scale)
    {
        var num = scale / 100f;
        var x = (markerX - 1024f) / num;
        var y = (markerY - 1024f) / num;
        return new Vector2(x, y);
    }
}
#endif
