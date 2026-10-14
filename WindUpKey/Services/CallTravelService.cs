using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Party;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using WindUpKey.Protocol;
using WindUpKey.Ui;

namespace WindUpKey.Services;

/// <summary>
/// Answers an owner call by traveling near the owner's position via Lifestream + vnavmesh.
/// </summary>
public sealed class CallTravelService : IDisposable
{
    private const string CallErrorEcho = "Your owner's Call ran into a problem.";
    /// <summary>11-D3: the owner gets the same Failed result; only the doll's line names the missing plugins.</summary>
    private const string NoTravelPluginsEcho =
        "Your owner tried to Call you. Lifestream and vnavmesh must be installed and ready to answer Calls.";
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
    /// <summary>GeneralAction row: Mount Roulette. Internal so the self-test's post-patch check reads the same row (04-D8).</summary>
    internal const uint GeneralActionMountRoulette = 9;
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
    /// <summary>After our own Entrance interaction, wait this long for its Yes/No before interacting again.</summary>
    private const long HouseEnterConfirmWaitMs = 4_000;
    private const string CallExpiredEcho = "Your owner's Call expired.";
    private const string CallStuckEcho = "Couldn't reach your owner; the Call was stopped.";
    /// <summary>07-B3: <c>TerritoryType.TerritoryIntendedUse</c> of a housing interior (ClientStructs <c>HousingIndoor</c>).</summary>
    private const uint HousingIndoorIntendedUse = (uint)FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse.HousingIndoor;
    private const string ApartmentConfirmHint =
        "Press Yes to enter your owner's apartment (Wind-Up Key can't confirm it in this client language).";

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
    /// <summary>07-B3: the owner's party-list territory, checked once when a Call arrives.</summary>
    private readonly IPartyList _party;
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
    /// <summary>07-A9: <c>ConsentService.CanReceiveCallFromKey</c>, re-checked every tick.</summary>
    private readonly Func<string, bool> _canReceiveCallFromKey;
    /// <summary>Monotonic milliseconds (<c>Environment.TickCount64</c>) for the 07 rules.</summary>
    private readonly Func<long> _clock;
    private readonly ICallSheetFacts _sheets;
    private readonly CallLandingGrace _landing;
    private readonly CallAckGate _ackGate;
    private readonly CallDeadlines _deadlines;
    private readonly CallStuckDetector _stuck;
    /// <summary>07-A11, 07-A15: vnav's current waypoint for the stuck detector, and whether its IPC is missing this Call.</summary>
    private readonly CallWaypointReader _waypoints;
    /// <summary>07-B1 (D4): five minutes of automatic declines per owner after her Decline (in memory only).</summary>
    private readonly CallQuietPeriod _quiet;

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
    /// <summary>07-A11: vnav's waypoint count, polled each pathing tick (vnavmesh 1.2.3.14 and later).</summary>
    private ICallGateSubscriber<int>? _vnavNumWaypoints;
    /// <summary>07-A11: vnav's waypoints; it allocates, so it is read only when the count changes.</summary>
    private ICallGateSubscriber<List<Vector3>>? _vnavListWaypoints;

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
    /// <summary>
    /// 07-B1: Decline (or Stop) from the prompt's Draw, or <c>/windup stopcall</c>; the next tick declines the Call, in
    /// any state. Cleared by <see cref="SetPhase"/> (a click from before the change, or for an earlier Call).
    /// </summary>
    private bool _declineRequested;
    /// <summary>
    /// 07-B1 (D3): her Accept, remembered for this Call: travel starts when the gates clear, with no second prompt. A
    /// failure asks her again. Not cleared by <see cref="SetPhase"/>.
    /// </summary>
    private bool _callAccepted;
    /// <summary>07-B2: when we last aborted our own Lifestream/vnav automation (monotonic ms), across Calls; null before.</summary>
    private long? _lastOwnAbortMs;
    /// <summary>07-A9: the Debug tab's Store and Recall, raised from Draw and run on the next tick.</summary>
    private bool _storeDebugRequested;
    private bool _recallDebugRequested;
    private bool _houseEntrancePathStarted;
    /// <summary>Where the current door approach is pathing to (the stuck detector's target, 07-A4).</summary>
    private Vector3? _houseEntranceDest;
    /// <summary>
    /// 07-A4: an indoor Call is walking to the door (set by the entry step). 07-A15: it holds across the entry step's
    /// throttled ticks and is cleared only where she really isn't approaching (in transit, off the ward, at the door
    /// interacting or confirming, dismounting, inside) or the phase changes.
    /// </summary>
    private bool _doorApproachActive;
    private bool _pathingSeenBusy;
    /// <summary>07-A1: the input mute as last applied by <see cref="ApplyLockPolicy"/>.</summary>
    private bool _muteOn;
    private bool _apartmentHintShown;
    /// <summary>07-A10: the last "yesno: not confirmed" detail logged; it is logged again only when it changes.</summary>
    private string? _lastYesnoIgnoredDetail;
    private DateTimeOffset _lastMountAttemptUtc;
    private DateTimeOffset _lastHouseEnterAttemptUtc;
    /// <summary>07-A6: when this Call last interacted with an Entrance itself (monotonic ms), or null.</summary>
    private long? _houseEnterInteractedMs;
    /// <summary>07-A6: when <c>MansionSelectRoom</c> was last visible during an apartment Call (monotonic ms), or null.</summary>
    private long? _selectRoomSeenMs;
    private DateTimeOffset _lastTeleportRetryUtc;
    private DateTimeOffset? _pathStartDeadlineUtc;
    private DateTimeOffset? _mountDeadlineUtc;
    private bool _localDebugCall;
    private CallPayload? _debugStoredPoint;
    private DateTimeOffset _stepStartedUtc;
    /// <summary>07-A1: changed only by <see cref="SetPhase"/>.</summary>
    private CallPhase _phase = CallPhase.Idle;
    private string? _lastCallDebugKey;
    private DateTimeOffset _lastCallDebugUtc;

    private sealed class PendingCall
    {
        public required string RequestId { get; init; }
        public required string OwnerKey { get; init; }
        /// <summary>07-A5 (K1): the payload's WorldName is never kept; travel resolves the name from this id.</summary>
        public required uint WorldId { get; init; }
        public required uint TerritoryId { get; init; }
        public required Vector3 Position { get; init; }
        public int HousingCity { get; init; }
        public int HousingWard { get; init; }
        public int HousingDivision { get; init; }
        public int HousingPlot { get; init; }
        public int HousingApartment { get; init; }
        public bool HousingIsApartment { get; init; }
        public bool HousingIndoor { get; init; }

        /// <summary>07-B4 (D13): the prompt's destination, from Lumina names and the Call's numbers (never payload text).</summary>
        public string Destination { get; set; } = string.Empty;

        public bool IsHousingCall =>
            HousingWard > 0 && HousingCity != 0 && HousingDivision is 1 or 2;
    }

    public CallTravelService(
        IDalamudPluginInterface pi,
        IClientState clientState,
        IObjectTable objects,
        IPartyList party,
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
        Func<CallResultPayload, Task> sendResult,
        Func<string, bool> canReceiveCallFromKey,
        Func<long>? clock = null)
    {
        _pi = pi;
        _clientState = clientState;
        _objects = objects;
        _party = party;
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
        _canReceiveCallFromKey = canReceiveCallFromKey;
        _clock = clock ?? (() => Environment.TickCount64);
        _sheets = new LuminaCallSheets(data);
        _landing = new CallLandingGrace(_clock);
        _ackGate = new CallAckGate(_clock);
        _deadlines = new CallDeadlines(_clock);
        _stuck = new CallStuckDetector(_clock);
        // 07-A15: an older vnavmesh has no waypoint IPC; IpcNotReadyError says so, and the reader remembers it.
        _waypoints = new CallWaypointReader(
            () => _vnavNumWaypoints?.InvokeFunc(),
            () => _vnavListWaypoints?.InvokeFunc(),
            ex => ex is IpcNotReadyError);
        _quiet = new CallQuietPeriod(_clock);

        _prompt.OnAccept = OnPromptAccept;
        _prompt.OnDecline = OnPromptDecline;
        BindIpc();
    }

    public bool IsTravelReady => ProbeTravelReady();

    public bool HasActiveCall => _pending is not null;

    /// <summary>07-A1: a finished Call still keeps the bypass until she has landed (the self-test's X-call-idle).</summary>
    public bool IsLandingGraceActive => _landing.IsActive;

    /// <summary>True when <see cref="TryStoreDebugPoint"/> has captured a recall target this session.</summary>
    public bool HasDebugStoredPoint => _debugStoredPoint is not null;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _prompt.OnAccept = null;
        _prompt.OnDecline = null;
        _prompt.IsOpen = false;
        // Unloading: nothing is left on, not even a landing grace.
        CancelInternal(landingGrace: false);
    }

    /// <summary>
    /// A silent cancel (<see cref="CallResultStatuses.Cancelled"/>): the safeword (06-B1) and logout, in Hardcore too.
    /// A cancel is never a Decline and starts no quiet period (07-B1).
    /// </summary>
    public void CancelActiveCall()
    {
        if (_pending is null)
            return;
        Forget(FinishAsync(CallResultStatuses.Cancelled, "Call cancelled."), "cancel result");
    }

    /// <summary>
    /// 07-B1: <c>/windup stopcall</c> (non-Hardcore; Plugin refuses it in Hardcore, D5). Stop counts as a Decline. Only
    /// sets a flag, which the next tick consumes. False when there is no Call to stop.
    /// </summary>
    public bool RequestDecline()
    {
        if (_pending is null)
            return false;
        _declineRequested = true;
        return true;
    }

    /// <summary>
    /// 07-A9: the Debug tab's Store, from ConfigWindow's Draw. Only sets a flag: the next tick stores the point, as it
    /// reads housing state and the recall runs IPC and <c>SyncLockState</c>.
    /// </summary>
    public void RequestStoreDebugPoint() => _storeDebugRequested = true;

    /// <summary>07-A9: the Debug tab's Recall, from ConfigWindow's Draw; run on the next tick.</summary>
    public void RequestRecallDebugPoint() => _recallDebugRequested = true;

    /// <summary>Framework thread: runs a Store or Recall raised from the Debug tab since the last tick.</summary>
    private void TickDebugRequests()
    {
        if (_storeDebugRequested)
        {
            _storeDebugRequested = false;
            TryStoreDebugPoint();
        }

        if (_recallDebugRequested)
        {
            _recallDebugRequested = false;
            TryRecallDebugPoint();
        }
    }

    /// <summary>
    /// Debug: snapshot the local player's current call destination (position + housing ward/division).
    /// </summary>
    private bool TryStoreDebugPoint()
    {
        if (_disposed)
            return false;

        if (_objects.LocalPlayer is not { } player || !_clientState.IsLoggedIn)
        {
            PluginChat.PrintError(_chat, "You must be in the world to store a call point.");
            return false;
        }

        var territoryId = _clientState.TerritoryType;
        var stored = new CallPayload
        {
            RequestId = string.Empty,
            From = _config.PairingKey,
            To = _config.PairingKey,
            WorldId = player.CurrentWorld.RowId,
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
    /// Debug: start Call travel to the last stored point (local only — no relay), through the same
    /// destination rules as an owner's Call (07-A5). No-op when nothing has been stored.
    /// </summary>
    private bool TryRecallDebugPoint()
    {
        if (_disposed || _debugStoredPoint is null)
            return false;

        if (!_config.IsDoll)
        {
            PluginChat.PrintError(_chat, "Call recall is only available as a Doll.");
            return false;
        }

        // 07-A10: refused during the safeword pause, before RequestAsync starts anything (07-B2: the recall needs no
        // Accept, so it stops her own travel as soon as the gates clear).
        if (_timer.IsRemoteControlPaused)
        {
            PluginChat.PrintError(_chat, "Call recall isn't available while remote control is paused.");
            return false;
        }

        var payload = CloneCallPayload(_debugStoredPoint);
        payload.RequestId = Guid.NewGuid().ToString("N");
        payload.From = PairingKeyUtil.IsValid(_config.PairingKey)
            ? _config.PairingKey
            : "DEBUGCALL";
        payload.To = payload.From;
        Forget(RequestAsync(payload, localDebug: true), "debug recall");
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
                "request rejected: already answering another call",
                $"incoming terr={payload.TerritoryId} world={payload.WorldId}");
            await SendResultOrLocalAsync(payload, CallResultStatuses.Failed, "Already answering another call.", localDebug)
                .ConfigureAwait(false);
            return;
        }

        // 07-B1 (D4): this owner's Calls are declined straight away for 5 minutes after her Decline, with a chat line
        // (not in Hardcore, where she can't decline, D5). The quiet period isn't extended by this.
        var callerKey = PairingKeyUtil.Normalize(payload.From);
        if (!localDebug
            && CallAskFirst.AutoDeclines(_config.HardcoreMode, _quiet.IsQuiet(callerKey, out var quietMinutes)))
        {
            DebugCall("request declined: quiet period after her Decline", throttle: false);
            PluginChat.Print(
                _chat,
                $"Declined {OwnerLabel(callerKey)}'s Call (quiet for {quietMinutes} more min).",
                PluginChat.Grey);
            await SendResultOrLocalAsync(payload, CallResultStatuses.Declined, "Call declined.", localDebug)
                .ConfigureAwait(false);
            return;
        }

        // 07-A5: the destination's numbers are checked against the sheets before anything else (debug recall too).
        // 07-A10: her territory too, so a same-zone Call in an aethernet-only city needs no aetheryte there.
        var validation = CallRequestRules.Validate(
            payload,
            new CallDollContext(_objects.LocalPlayer?.CurrentWorld.RowId ?? 0, _clientState.TerritoryType),
            _sheets);
        if (!validation.Ok)
        {
            ReportEdgeCaseError(
                $"request rejected: {validation.Reason}",
                $"terr={payload.TerritoryId} world={payload.WorldId}");
            await SendResultOrLocalAsync(payload, CallResultStatuses.Failed, $"Call refused: {validation.Reason}.", localDebug)
                .ConfigureAwait(false);
            return;
        }

        // 07-B3 (D2): once, on arrival, against her party list and object table: an owner she can check must be where
        // the Call says. Nothing to check accepts. The debug recall skips it. The computed keys are never logged.
        if (!localDebug)
        {
            var corroboration = CallCorroboration.Check(payload, OwnerPartyPlace(callerKey), OwnerSighting(callerKey));
            if (!corroboration.Ok)
            {
                ReportEdgeCaseError(
                    $"request rejected: {corroboration.Reason}",
                    $"terr={payload.TerritoryId} world={payload.WorldId}");
                await SendResultOrLocalAsync(payload, CallResultStatuses.Failed, $"Call refused: {corroboration.Reason}.", localDebug)
                    .ConfigureAwait(false);
                return;
            }
        }

        // vnavmesh may report not-ready while combat/instance/crafting already prevents travel.
        // Preserve the Call and show its normal wait state; probe again when Accept can become active.
        var hasDeferredGameGate = IsCrafting() || IsInInstance() || IsInCombat();
        if (!hasDeferredGameGate && !IsTravelReady)
        {
            ReportEdgeCaseError(
                "request rejected: travel plugins unavailable",
                $"terr={payload.TerritoryId} world={payload.WorldId}",
                NoTravelPluginsEcho);
            await SendResultOrLocalAsync(payload, CallResultStatuses.Failed, "Lifestream and vnavmesh are required.", localDebug)
                .ConfigureAwait(false);
            return;
        }

        var ownerKey = callerKey;
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
        _houseEntranceDest = null;
        _doorApproachActive = false;
        _pathingSeenBusy = false;
        _apartmentHintShown = false;
        _lastYesnoIgnoredDetail = null;
        _lastMountAttemptUtc = default;
        _lastHouseEnterAttemptUtc = default;
        _houseEnterInteractedMs = null;
        _selectRoomSeenMs = null;
        _lastTeleportRetryUtc = default;
        _pathStartDeadlineUtc = null;
        _mountDeadlineUtc = null;
        _weOwnTravel = false;
        // 07-B1 (D3): a non-Hardcore Call waits for her Accept. Her own debug recall needs none.
        _callAccepted = localDebug;
        _pending.Destination = DescribeDestination(_pending);
        _lastCallDebugKey = null;
        _ackGate.Reset();
        _deadlines.Start();
        ResetStuckDetection(newCall: true);
        // 07-A1: no bypass here. It goes on in BeginTravel, so an unwound doll stays locked while the Call waits for a
        // duty, combat, crafting or her answer.
        SetPhase(CallPhase.WaitingGates);
        // 07-B2: her own Lifestream or vnav trip isn't aborted here any more. The prompt shows it as Busy, and it is
        // stopped only once she accepts (or Hardcore starts on its own); our own automation still draining after an
        // earlier Call's abort is aborted again instead (IsHerTravelBusy).

        DebugCall($"accepted request → {FormatPendingSummary(_pending)}", throttle: false);

        if (localDebug)
            PluginChat.Print(_chat, "Debug call recall started.", PluginChat.Yellow);

        if (_ackGate.Offer(ClassifyGateStatus()) is { } status)
            await AckAsync(status).ConfigureAwait(false);
    }

    public void Tick()
    {
        if (_disposed)
            return;

        TickDebugRequests();
        if (_pending is null)
        {
            TickLandingGrace();
            return;
        }

        try
        {
            // After aetheryte / housing travel, LocalPlayer is briefly null while still logged in.
            // Treating that as logout cancelled the Call the moment the doll landed.
            if (!_clientState.IsLoggedIn)
            {
                Forget(FinishAsync(CallResultStatuses.Cancelled, "Logged out."), "logout result");
                return;
            }

            // 07-A9: unpaired, no longer allowed to call her, or her safeword paused remote control: silently cancelled.
            if (ConsentLost())
            {
                DebugCall(CallLogRules.Forced("cancelled: consent lost", _phase, PendingKind()), throttle: false, force: true);
                Forget(FinishAsync(CallResultStatuses.Cancelled, "Call cancelled."), "consent result");
                return;
            }

            // 07-B1 (D4): her Decline or Stop, raised since the last tick, in any state (never in Hardcore, D5).
            if (_declineRequested)
            {
                _declineRequested = false;
                if (TryDeclineActiveCall())
                    return;
            }

            ApplyLockPolicy();
            FlushAck();
            if (TickDeadlines())
                return;

            if (_objects.LocalPlayer is null)
                return;

            TrackSelectRoom();
            SyncLifestreamDrivingRmi();
            if (TickStuckDetection())
                return;

            // 07-B1: the small "Answering…" window (with Stop outside Hardcore) while the Call drives her.
            if (CallLockPolicy.IsTravelPhase(_phase))
                ShowTravelWindow();

            switch (_phase)
            {
                case CallPhase.WaitingGates:
                case CallPhase.WaitingAccept:
                    TickWaiting();
                    break;
                case CallPhase.ChangingWorld:
                    TickChangingWorld();
                    break;
                case CallPhase.Teleporting:
                    TickTeleporting();
                    break;
                case CallPhase.Pathing:
                    _timer.SetCallTravelMuteDestination(_pending.Position);
                    TickPathing();
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Call travel tick failed");
            try
            {
                EnterFailedPrompt("Unhandled tick exception.", ex.GetType().Name);
            }
            catch (Exception inner)
            {
                // 07-A1: the exception path still restores the lock and the mute.
                _log.Warning(inner, "Call travel failed prompt failed");
                CancelInternal();
            }
        }
    }

    /// <summary>
    /// 07-A1: the one place the phase changes. It applies <see cref="CallLockPolicy"/> (bypass and mute only while
    /// travelling, a landing grace when a travel phase ends in the air) and drops a click from before the change.
    /// </summary>
    private void SetPhase(CallPhase next)
    {
        var previous = _phase;
        if (CallLockPolicy.IsTravelPhase(next))
            _landing.End();
        else if (CallLockPolicy.IsTravelPhase(previous))
            _landing.Begin(IsAirborne());

        _phase = next;
        // 07-A15: a door approach belongs to the Teleporting step it started in.
        if (previous != next)
            _doorApproachActive = false;
        // A stale accept: Accept clicked, then a duty or zone load in the same tick, must not accept the next prompt
        // by itself. 07-B1: nor a stale Decline decline a later Call. (Her remembered Accept, _callAccepted, stays.)
        _acceptRequested = false;
        _declineRequested = false;
        ApplyLockPolicy();
        if (previous != next)
            DebugCall($"phase {previous} → {next}", throttle: false);
    }

    /// <summary>07-A1: sets the bypass and the mute for the current phase and landing grace (each tick and on each change).</summary>
    private void ApplyLockPolicy()
    {
        var landing = _landing.IsActive && _landing.Update(IsAirborne());
        var state = CallLockPolicy.For(_phase, landing);
        // The bypass first: turning it on runs SyncLockState, which must unlock before any travel call.
        _timer.SetCallTravelBypass(state.Bypass);
        if (state.Mute == _muteOn)
            return;

        _muteOn = state.Mute;
        _timer.SetCallTravelInputMute(state.Mute);
    }

    /// <summary>No Call left, but a landing grace may still hold the bypass: it ends on landing or after 60 s (07-A1).</summary>
    private void TickLandingGrace()
    {
        // 07-A10: the release is decided by the Dalamud-free rule (unit-tested).
        if (_landing.ReleaseBypass(IsAirborne()))
            _timer.SetCallTravelBypass(false);
    }

    private bool IsAirborne() => _condition[ConditionFlag.InFlight] || _condition[ConditionFlag.Diving];

    /// <summary>07-A9: the Call's consent no longer holds (checked every tick, after it was accepted on arrival).</summary>
    private bool ConsentLost()
    {
        if (_pending is null)
            return false;
        // Second line of defence for 06-B1: the safeword command cancels the Call itself, right after pausing.
        if (!_config.IsDoll || _timer.IsRemoteControlPaused)
            return true;
        // The debug recall is her own, with no owner to consent.
        return !_localDebugCall && !_canReceiveCallFromKey(_pending.OwnerKey);
    }

    /// <summary>07-A3 (D7): ends a Call past its wait, failed-prompt or overall limit, with a chat line. True when it ended.</summary>
    private bool TickDeadlines()
    {
        // 07-B1: WaitingAccept is only the failed prompt now.
        var kind = CallLockPolicy.IsTravelPhase(_phase)
            ? CallWaitKind.Traveling
            : _phase == CallPhase.WaitingAccept
                ? CallWaitKind.FailedPrompt
                : CallWaitKind.Waiting;
        var expiry = _deadlines.Check(kind, IsBetweenAreas());
        if (expiry == CallExpiry.None)
            return false;

        DebugCall(CallLogRules.Forced($"expired: {expiry}", _phase, PendingKind()), throttle: false, force: true);
        PluginChat.Print(_chat, CallExpiredEcho, PluginChat.Yellow);
        Forget(FinishAsync(CallResultStatuses.Failed, "Call expired."), "expiry result");
        return true;
    }

    /// <summary>
    /// 07-A4 (D6), 07-D14: feeds the stuck detector while pathing (to the owner, or to the door of an indoor Call) and
    /// stops a Call that makes no headway, in Hardcore too. True when it ended.
    /// </summary>
    private bool TickStuckDetection()
    {
        if (_pending is null || _objects.LocalPlayer is not { } player)
            return false;

        if (!CallLockPolicy.IsTravelPhase(_phase))
        {
            ResetStuckDetection(newCall: false);
            return false;
        }

        Vector3 target;
        if (_phase == CallPhase.Pathing)
            target = _pending.Position;
        else if (_phase == CallPhase.Teleporting && _doorApproachActive && _houseEntranceDest is { } door)
            target = door;
        else
        {
            // A travel step that isn't pathing (world change, teleport, mount, mesh build): its time doesn't count.
            _stuck.Pause();
            return false;
        }

        // 07-D14 / 07-A11: headway is measured to vnav's next waypoint (the final target without one), in 3D while
        // flying or diving and XZ on the ground; the final target is the reset key and drives the door pause.
        var position = player.Position;
        var waypointIpcMissing = _waypoints.Missing;
        var node = _waypoints.Read(target, out var waypointCount, out var waypointsAvailable);
        // 07-A15: an older vnavmesh (no waypoint IPC) keeps only the can't-path check and the step timeout; said once a Call.
        if (_waypoints.Missing && !waypointIpcMissing)
            DebugCall("stuck: vnavmesh has no waypoint IPC (older than 1.2.3.14); no-progress check off", throttle: false);
        var airborne = IsAirborne();
        var distance = CallStuckDetector.ProgressDistance(position, node, airborne);
        var paused = IsBetweenAreas()
                     || IsMountTransition()
                     || IsPlayerCasting()
                     // At the door, interacting and its Yes/No take over.
                     || (_phase == CallPhase.Teleporting && Vector3.Distance(position, target) <= HouseEntranceInteractYalms);
        var reason = _stuck.Observe(
            target,
            node,
            waypointCount,
            waypointsAvailable,
            distance,
            airborne,
            IsVnavPathRunning(),
            IsVnavPathfindPending(),
            paused);
        if (reason == CallStuckReason.None)
            return false;

        DebugCall(CallLogRules.Forced($"stuck: {reason}", _phase, PendingKind()), throttle: false, force: true);
        // Ours only: never another plugin's travel.
        if (_weOwnTravel)
            AbortCallAutomation();
        PluginChat.Print(_chat, CallStuckEcho, PluginChat.Yellow);
        Forget(FinishAsync(CallResultStatuses.Failed, "Couldn't reach the destination."), "stuck result");
        return true;
    }

    /// <summary>
    /// 07-D14: forget the stuck detector's target and the cached vnav waypoint (a waiting phase, a new Call, a cancel).
    /// 07-A15: a new Call also tries vnav's waypoint IPC again.
    /// </summary>
    private void ResetStuckDetection(bool newCall)
    {
        _stuck.Reset();
        _waypoints.Reset(newCall);
    }

    private bool IsMountTransition() =>
        _condition[ConditionFlag.Mounting]
        || _condition[ConditionFlag.Mounting71]
        || _condition[ConditionFlag.MountOrOrnamentTransition];

    /// <summary>07-A6: when the apartment room list was last visible (apartment Calls only).</summary>
    private void TrackSelectRoom()
    {
        if (_pending is not { HousingIsApartment: true })
            return;

        var addon = _gameGui.GetAddonByName("MansionSelectRoom");
        if (!addon.IsNull && addon.IsVisible)
            _selectRoomSeenMs = _clock();
    }

    private string PendingKind() =>
        _pending is null
            ? "none"
            : CallRequestRules.CoarseKind(_pending.IsHousingCall, _pending.HousingIndoor, _pending.HousingIsApartment);

    /// <summary>
    /// 07-A9: a send nobody awaits (from the tick or a UI callback). A failure is logged at Debug, never left
    /// unobserved (through the shared helper since 08-P1).
    /// </summary>
    private void Forget(Task task, string what) =>
        FireAndForget.Run(task, ex => _log.Debug(ex, "Call {What} failed", what));

    /// <summary>07-A2: an ack through the gate (only a changed status, at most one every 2 s).</summary>
    private void Ack(string status)
    {
        if (_ackGate.Offer(status) is { } send)
            Forget(AckAsync(send), "ack");
    }

    /// <summary>07-A2: an ack the gate held back, once its floor has passed.</summary>
    private void FlushAck()
    {
        if (_ackGate.Flush() is { } held)
            Forget(AckAsync(held), "ack");
    }

    /// <summary>
    /// 07-B1 (D3, D5), 07-B2: a Call that isn't travelling. Her Accept (from the prompt's Draw) is remembered for the
    /// Call; <see cref="CallAskFirst.Decide"/> rules whether it travels now. While it waits, the prompt shows who is
    /// calling, where to, and what is in the way: Accept and Decline outside Hardcore, information only in Hardcore
    /// (a failed prompt keeps its Accept, the retry). WaitingGates waits before travel; WaitingAccept is the failed
    /// prompt.
    /// </summary>
    private void TickWaiting()
    {
        if (_pending is null)
            return;

        var hardcore = _config.HardcoreMode;

        if (_acceptRequested)
        {
            _acceptRequested = false;
            // 07-B6: the rule remembers her Accept; on the failed prompt it is the retry, back to WaitingGates.
            var step = CallAskFirst.OnAccept(hardcore, _callAccepted, _phase);
            _callAccepted = step.Accepted;
            if (step.Phase != _phase)
            {
                // A failed retry: the attempt that just failed may still be draining Lifestream/vnav.
                AbortCallAutomation();
                SetPhase(step.Phase);
            }
        }

        // Read after her Accept: a retry is no longer failed.
        var failed = _phase == CallPhase.WaitingAccept;
        var gate = CurrentGate();
        // 07-B2: her own trip shows as Busy until she accepts; read once a tick (it may abort our own draining).
        // 07-B6: never on the failed prompt, so a trip she starts right after a failure isn't aborted without her Accept.
        var herTravel = CallAskFirst.ReadsHerTravel(failed, gate) && IsHerTravelBusy();
        switch (CallAskFirst.Decide(hardcore, _callAccepted, failed, gate, herTravel))
        {
            case CallStart.StopHerTravelThenTravel:
                // 07-B2: only now, after her Accept (or Hardcore's own start), is her own trip stopped.
                DebugCall("start: stopping her own travel first", throttle: false);
                AbortCallAutomation();
                BeginTravel();
                return;
            case CallStart.Travel:
                BeginTravel();
                return;
        }

        if (gate == CallGate.Crafting && !_craftNotified)
        {
            _craftNotified = true;
            PluginChat.Print(_chat, PresenceRequiredEcho(), PluginChat.Yellow);
        }

        // 07-A2: every waiting tick lands here (a duty or zone load too); the gate sends a changed status once.
        if (!failed)
            Ack(AckStatusFor(gate));

        ShowWaitingPrompt(failed ? CallPromptReason.Failed : PromptReasonFor(gate, herTravel), hardcore, failed);
    }

    /// <summary>07-B1: the Accept button's callback, from the prompt's Draw. Only sets a flag; the next tick consumes it.</summary>
    private void OnPromptAccept() => _acceptRequested = true;

    /// <summary>07-B1: the Decline or Stop button's callback, from the prompt's Draw. Only sets a flag; the next tick consumes it.</summary>
    private void OnPromptDecline() => _declineRequested = true;

    /// <summary>
    /// 07-B1 (D4): her Decline, or Stop during travel: <see cref="CallResultStatuses.Declined"/> to the owner, our travel
    /// stopped and the lock restored (<see cref="CancelInternal"/>), and that owner's quiet period started (in
    /// <see cref="FinishAsync"/>). Never in Hardcore (D5). True when the Call ended.
    /// </summary>
    private bool TryDeclineActiveCall()
    {
        if (_pending is null || !CallAskFirst.MayDecline(_config.HardcoreMode))
            return false;

        DebugCall($"declined by her in {_phase}", throttle: false);
        PluginChat.Print(_chat, $"You declined {OwnerLabel(_pending.OwnerKey)}'s Call.", PluginChat.Grey);
        Forget(FinishAsync(CallResultStatuses.Declined, "Call declined."), "decline result");
        return true;
    }

    /// <summary>07-B1: the first gate that keeps a Call from travelling (her own travel is <see cref="IsHerTravelBusy"/>).</summary>
    private CallGate CurrentGate()
    {
        if (IsCrafting())
            return CallGate.Crafting;
        if (IsInInstance())
            return CallGate.Instance;
        if (IsInCombat())
            return CallGate.Combat;
        // vnavmesh may report not-ready briefly after a zone load: waited out, within the wait limit.
        return IsTravelReady ? CallGate.None : CallGate.NotReady;
    }

    /// <summary>The ack for a waiting Call: its gate, or busy while it waits for her answer, her own trip or readiness.</summary>
    private static string AckStatusFor(CallGate gate) => gate switch
    {
        CallGate.Crafting => CallAckStatuses.Crafting,
        CallGate.Instance => CallAckStatuses.Instance,
        CallGate.Combat => CallAckStatuses.Combat,
        _ => CallAckStatuses.Busy,
    };

    private static CallPromptReason PromptReasonFor(CallGate gate, bool herTravel) => gate switch
    {
        CallGate.Crafting => CallPromptReason.Crafting,
        CallGate.Instance => CallPromptReason.Instance,
        CallGate.Combat => CallPromptReason.Combat,
        CallGate.NotReady => CallPromptReason.NotReady,
        _ => herTravel ? CallPromptReason.Busy : CallPromptReason.Asking,
    };

    /// <summary>07-B1, 07-B4: the waiting prompt, set each tick (Esc and a close button can't dismiss it).</summary>
    private void ShowWaitingPrompt(CallPromptReason reason, bool hardcore, bool failed)
    {
        if (_pending is null)
            return;

        _prompt.Reason = reason;
        _prompt.OwnerLabel = OwnerLabel(_pending.OwnerKey);
        _prompt.Destination = _pending.Destination;
        _prompt.Accepted = _callAccepted;
        _prompt.Hardcore = hardcore;
        _prompt.ShowAccept = CallAskFirst.OffersAccept(hardcore, _callAccepted, failed);
        _prompt.ShowDecline = CallAskFirst.MayDecline(hardcore);
        _prompt.IsOpen = true;
    }

    /// <summary>07-B1: the small non-modal "Answering &lt;label&gt;'s Call…" window during travel; Stop only outside Hardcore.</summary>
    private void ShowTravelWindow()
    {
        if (_pending is null)
            return;

        var hardcore = _config.HardcoreMode;
        _prompt.Reason = CallPromptReason.Travelling;
        _prompt.OwnerLabel = OwnerLabel(_pending.OwnerKey);
        _prompt.Hardcore = hardcore;
        _prompt.ShowAccept = false;
        _prompt.ShowDecline = CallAskFirst.MayDecline(hardcore);
        _prompt.IsOpen = true;
    }

    private void BeginTravel()
    {
        if (_pending is null)
            return;

        _subdivisionHopAttempted = false;
        _housingCityTeleportAttempted = false;
        _housingGoRetryAttempted = false;
        _teleportProgressSeen = false;
        _lastMountAttemptUtc = default;
        _pathStartDeadlineUtc = null;
        _mountDeadlineUtc = null;
        _lastCallDebugKey = null;
        var localWorld = _objects.LocalPlayer?.CurrentWorld.RowId ?? 0;
        var changeWorld = localWorld != 0 && localWorld != _pending.WorldId;
        // 07-A1: a travel phase before any travel call, so the bypass is on and SyncLockState has unlocked (and stood
        // her up) first. It also mutes input, but nothing steers toward the destination until local pathing —
        // otherwise we walk housing coords through the current zone's mesh (into walls).
        SetPhase(changeWorld ? CallPhase.ChangingWorld : CallPhase.Teleporting);
        // 07-B1: the prompt becomes the small "Answering…" window (Stop outside Hardcore).
        ShowTravelWindow();
        Ack(CallAckStatuses.Traveling);
        PluginChat.Print(_chat, PresenceRequiredEcho(), PluginChat.Yellow);
        DebugCall($"begin travel → {FormatPendingSummary(_pending)} | {FormatLocalHousingSnapshot()}", throttle: false);

        if (changeWorld)
        {
            DebugCall($"change world {localWorld} → {_pending.WorldId}", throttle: false);
            if (!TryChangeWorld(_pending))
            {
                EnterFailedPrompt("Could not change world.");
                return;
            }

            _weOwnTravel = true;
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
            EnterFailedPrompt("World change timed out.");
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
                SetPhase(CallPhase.Teleporting);
                _pathStartDeadlineUtc ??= DateTimeOffset.UtcNow + PathStartGrace;
                _stepStartedUtc = DateTimeOffset.UtcNow;
                return;
            }

            if (IsVnavPathfindPending())
            {
                DebugCall("start: waiting for leftover vnav pathfind", key: "start-vnav-pending");
                _weOwnTravel = true;
                SetPhase(CallPhase.Teleporting);
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
                SetPhase(CallPhase.Teleporting);
                _stepStartedUtc = DateTimeOffset.UtcNow;
                return;
            }

            if (!TryStartLocalPath())
            {
                DebugCall($"start: path start deferred | {DescribePathStartFailure()}", key: "start-path-defer");
                // Mesh often not ready on the first tick in-zone — keep trying in Teleporting.
                _weOwnTravel = true;
                SetPhase(CallPhase.Teleporting);
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
            SetPhase(CallPhase.Teleporting);
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
        SetPhase(CallPhase.Teleporting);
        _stepStartedUtc = DateTimeOffset.UtcNow;
    }

    private void TickTeleporting()
    {
        if (_pending is null)
            return;

        // 07-A15: the door-approach flag isn't cleared here every tick. The entry step sets it while pathing to the door
        // and leaves it alone on its 250 ms throttled ticks (14 in 15), so the stuck detector, which reads it next tick,
        // isn't paused through a blocked approach. Each branch below that isn't approaching clears it.

        // Lifestream remains busy while waiting on the selected apartment's confirmation.
        // Confirm before the general busy early-return or the Call can wait here forever.
        if (_pending.IsHousingCall
            && _pending.HousingIndoor
            && _pending.HousingIsApartment
            && TryConfirmHousingEntranceYesno(apartment: true, out var apartmentYesnoDetail))
        {
            _doorApproachActive = false;
            _teleportProgressSeen = true;
            DebugCall($"enter-apartment: confirmed SelectYesno ({apartmentYesnoDetail})", key: "enter-apartment-yesno");
            return;
        }

        if (DateTimeOffset.UtcNow - _stepStartedUtc > TravelStepTimeout)
        {
            EnterFailedPrompt("Travel timed out.");
            return;
        }

        // Lifestream drives to the plot, which can sit past the owner. Once the doll is near the owner in their ward,
        // stop Lifestream and let vnavmesh take the last stretch (CallHandoffRules).
        if (_pending.IsHousingCall && IsLifestreamBusy() && ShouldHandOffToNavmesh())
        {
            try
            {
                _lsAbort?.InvokeAction();
            }
            catch
            {
                // ignore: the next tick sees whether Lifestream is still busy
            }

            _doorApproachActive = false;
            _teleportProgressSeen = true;
            DebugCall("teleport: near owner in ward — handing off from Lifestream to vnavmesh", key: "teleport-handoff");
            return;
        }

        // Aetheryte Teleport does not set Lifestream.IsBusy — only the cast + BetweenAreas.
        if (IsBetweenAreas() || IsLifestreamBusy() || IsPlayerCasting())
        {
            _doorApproachActive = false;
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
                _doorApproachActive = false;
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
                        _doorApproachActive = false;
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

                _doorApproachActive = false;
                StopOurPath();
                DebugCall("teleport: entered house — arrived (no indoor pathing)", throttle: false);
                Forget(FinishAsync(CallResultStatuses.Arrived, "Arrived at owner's house."), "arrived result");
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
            Forget(FinishAsync(CallResultStatuses.Arrived, "Arrived near owner."), "arrived result");
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
        SetPhase(CallPhase.Teleporting);
        _pathStartDeadlineUtc = DateTimeOffset.UtcNow + PathStartGrace;
        DebugCall("pathing: retry deferred to Teleporting", key: "pathing-defer");
    }

    /// <summary>
    /// A travel step failed: the failed prompt opens. <paramref name="detail"/> must be location-free (it goes in the
    /// forced line, 07-A8); the location goes in a debug line.
    /// </summary>
    private void EnterFailedPrompt(string detail, string? exceptionType = null)
    {
        if (_pending is null)
            return;

        DebugCall(CallLogRules.Forced($"FAILED: {detail}", _phase, PendingKind(), exceptionType), throttle: false, force: true);
        DebugCall($"FAILED at: {FormatPendingSummary(_pending)} | {FormatPathGateSummary()}", throttle: false);

        // 07-B2: ours only (the exception path can land here before any travel).
        AbortOurAutomation();
        _timer.SetCallTravelPathingPassThrough(false);
        _timer.SetCallTravelLifestreamDriving(false);
        _timer.SetCallTravelCastingFlyPass(false);
        PluginChat.Print(_chat, CallErrorEcho, PluginChat.Yellow);
        // 07-A1: out of travel, so the bypass and mute go off (after landing, when in the air): an unwound doll is
        // locked again, and sits, while the failed prompt waits. 07-A3: the failed-prompt limit starts over.
        SetPhase(CallPhase.WaitingAccept);
        // 07-B1: a failure asks her again (Accept retries, Decline ends it; Hardcore keeps only the retry).
        _callAccepted = false;
        ShowWaitingPrompt(CallPromptReason.Failed, _config.HardcoreMode, failed: true);
        _deadlines.MarkFailure();
        Ack(CallAckStatuses.Busy);
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
        CancelInternal();
        if (pending is null)
            return;

        if (localDebug)
        {
            if (string.Equals(status, CallResultStatuses.Arrived, StringComparison.Ordinal))
                PluginChat.Print(_chat, "Debug call recall arrived.", PluginChat.Green);
            else if (string.Equals(status, CallResultStatuses.Cancelled, StringComparison.Ordinal)
                     || string.Equals(status, CallResultStatuses.Declined, StringComparison.Ordinal))
                PluginChat.Print(_chat, "Debug call recall cancelled.", PluginChat.Grey);
            else
                PluginChat.Print(_chat, CallErrorEcho, PluginChat.Yellow);
            return;
        }

        // 07-B1 (D4): her Decline starts this owner's quiet period; a cancel (the safeword) or any other end doesn't.
        _quiet.Record(pending.OwnerKey, status);
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

    /// <summary>
    /// Ends the Call: the final safety net for every exit (arrival, failure, expiry, stuck, cancel, consent lost,
    /// logout, Dispose, the tick exception path). Nothing is left on but a landing grace's bypass (07-A1), and not even
    /// that with <paramref name="landingGrace"/> false (unloading).
    /// </summary>
    private void CancelInternal(bool landingGrace = true)
    {
        // 07-B2: our own travel only: a Call declined while she travels herself leaves her trip alone.
        AbortOurAutomation();
        SetPhase(CallPhase.Idle);
        if (!landingGrace)
            _landing.End();
        _prompt.IsOpen = false;
        _acceptRequested = false;
        _declineRequested = false;
        _callAccepted = false;
        _timer.SetCallTravelBypass(_landing.IsActive);
        _timer.SetCallTravelPathingPassThrough(false);
        _timer.SetCallTravelLifestreamDriving(false);
        _timer.SetCallTravelCastingFlyPass(false);
        _timer.SetCallTravelInputMute(false);
        _muteOn = false;
        ResetStuckDetection(newCall: false);
        _pending = null;
        _localDebugCall = false;
    }

    private static CallPayload CloneCallPayload(CallPayload source) =>
        new()
        {
            RequestId = source.RequestId,
            From = source.From,
            To = source.To,
            WorldId = source.WorldId,
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

    /// <summary>
    /// Stop vnavmesh and abort Lifestream so a cancelled/failed call cannot block the next one. 07-B2: called only for
    /// our own automation, or for her own trip once she has accepted (or Hardcore starts); the time is kept, so that
    /// busy travel in the next <see cref="CallAskFirst.OwnDrainMs"/> counts as ours draining.
    /// </summary>
    private void AbortCallAutomation()
    {
        StopAutomationIpc();
        _weOwnTravel = false;
        _lastOwnAbortMs = _clock();
    }

    /// <summary>07-B2: <see cref="AbortCallAutomation"/> only while the Call drives her (a travel phase, or our own travel).</summary>
    private void AbortOurAutomation()
    {
        if (_weOwnTravel || CallLockPolicy.IsTravelPhase(_phase))
            AbortCallAutomation();
    }

    /// <summary>The vnav stop and Lifestream abort IPC; failures are ignored.</summary>
    private void StopAutomationIpc()
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
    }

    /// <summary>The first ack: the gate that holds, or busy (07-B1: nothing travels before her answer or the next tick).</summary>
    private string ClassifyGateStatus() => AckStatusFor(CurrentGate());

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

    /// <summary>
    /// 07-B2: Lifestream or vnav busy with travel that isn't ours: her own trip (or another plugin's). Busy within
    /// <see cref="CallAskFirst.OwnDrainMs"/> of our own abort is our automation still draining: it is aborted again
    /// (without restarting that window) and isn't shown as Busy.
    /// </summary>
    private bool IsHerTravelBusy()
    {
        if (_weOwnTravel || !(IsLifestreamBusy() || IsVnavBusy()))
            return false;
        if (!CallAskFirst.IsOwnDrain(_lastOwnAbortMs, _clock()))
            return true;

        DebugCall("busy: our own automation still draining; aborted again", key: "own-drain");
        StopAutomationIpc();
        return false;
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

        // 07-A11: optional; without them the stuck detector measures to the final target.
        try
        {
            _vnavNumWaypoints = _pi.GetIpcSubscriber<int>("vnavmesh.Path.NumWaypoints");
            _vnavListWaypoints = _pi.GetIpcSubscriber<List<Vector3>>("vnavmesh.Path.ListWaypoints");
        }
        catch (Exception ex)
        {
            _vnavNumWaypoints = null;
            _vnavListWaypoints = null;
            _log.Debug(ex, "Call travel vnav waypoint IPC bind failed");
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
        var inTransitPhase = _phase is CallPhase.ChangingWorld or CallPhase.Teleporting;
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
            // 07-A5 (K1): the name fallback is resolved from WorldId, never taken from the payload.
            var worldName = ResolveWorldName(call.WorldId);
            if (!string.IsNullOrWhiteSpace(worldName)
                && _lsChangeWorld is not null
                && _lsChangeWorld.InvokeFunc(worldName))
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
        // 07-A5 (K1): resolved from WorldId; the payload's WorldName is never used.
        var worldName = ResolveWorldName(call.WorldId);
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
        SetPhase(CallPhase.Pathing);
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

    private bool ShouldHandOffToNavmesh()
    {
        var player = _objects.LocalPlayer;
        if (player is null || _pending is null)
            return false;

        var dx = player.Position.X - _pending.Position.X;
        var dz = player.Position.Z - _pending.Position.Z;
        return CallHandoffRules.ShouldHandOffToNavmesh(
            housingCall: _pending.IsHousingCall,
            indoor: _pending.HousingIndoor,
            atHousingWard: IsAtHousingWard(),
            betweenAreas: IsBetweenAreas(),
            casting: IsPlayerCasting(),
            distanceToOwnerYalms: MathF.Sqrt(dx * dx + dz * dz));
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
            // Lifestream walks an apartment Call; there's no door approach of ours.
            _doorApproachActive = false;
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
            _doorApproachActive = false;
            _houseEnterInteractedMs = null;
            _teleportProgressSeen = true;
            DebugCall($"enter-house: confirmed SelectYesno ({yesnoDetail})", key: "enter-yesno");
            return;
        }

        // Give our own interaction's Yes/No time to open before interacting again; a dialog that never matched or
        // never appeared allows another interact afterwards.
        if (_houseEnterInteractedMs is { } interacted && _clock() - interacted < HouseEnterConfirmWaitMs)
        {
            _doorApproachActive = false;
            DebugCall(
                $"enter-house: waiting for SelectYesno ({yesnoDetail})",
                key: "enter-wait-yesno");
            return;
        }

        // 07-A15: a throttled tick changes nothing, so the door-approach flag stays as the last attempt left it.
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
            _doorApproachActive = false;
            DebugCall("enter-house: waiting to dismount", key: "enter-dismount");
            return;
        }

        var entrance = FindNearestHouseEntrance(out var dist);
        if (entrance is not null && dist <= HouseEntranceInteractYalms)
        {
            _doorApproachActive = false;
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
                // 07-A6: the entrance prompt is accepted only within 5 s of this, our own interaction.
                _houseEnterInteractedMs = _clock();
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
            _doorApproachActive = _houseEntrancePathStarted;
            DebugCall(
                $"enter-house: approaching door (entrance={(entrance is null ? "none" : $"{dist:0.0}y")})",
                key: "enter-approach");
            return;
        }

        TryPathTowardPlotEntrance(player.Position, entrance, dist);
        _doorApproachActive = _houseEntrancePathStarted;
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
    /// 07-A6: press Yes on a housing SelectYesno only when <see cref="HousingYesnoMatcher"/> accepts it: the house
    /// entrance prompt (exactly this client language's text) within 5 s of our own Entrance interaction, or an apartment
    /// room confirm matching this language's pattern while Lifestream, in the right ward, has just shown the room list.
    /// In a language with no known apartment text she gets a chat hint instead (D10). Never an empty prompt.
    /// </summary>
    private unsafe bool TryConfirmHousingEntranceYesno(bool apartment, out string detail)
    {
        detail = "no addon";
        try
        {
            HousingYesnoContext? context = null;
            var language = HousingYesnoMatcher.FromClientLanguage(_clientState.ClientLanguage.ToString());
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

                // Read the Call's state once, only when a dialog is up.
                context ??= new HousingYesnoContext(
                    ApartmentCall: apartment,
                    NowMs: _clock(),
                    EntranceInteractedMs: _houseEnterInteractedMs,
                    InRightWard: apartment && IsAtHousingWard(),
                    LifestreamBusy: apartment && IsLifestreamBusy(),
                    SelectRoomSeenMs: _selectRoomSeenMs);

                switch (HousingYesnoMatcher.Decide(prompt, language, context.Value))
                {
                    case YesnoDecision.Confirm:
                        addon->FireCallbackInt(0); // Yes
                        // 07-A10: the confirmed lines carry no prompt text.
                        detail = $"addon#{index} → Yes";
                        return true;
                    case YesnoDecision.Hint:
                        if (!_apartmentHintShown)
                        {
                            _apartmentHintShown = true;
                            PluginChat.Print(_chat, ApartmentConfirmHint, PluginChat.Yellow);
                        }

                        detail = $"addon#{index} {HousingYesnoMatcher.PromptLogDetail(prompt, language, context.Value)}"
                                 + " → hint (no known apartment text for this language)";
                        continue;
                    default:
                        // 07-A10 (04-D10): the raw text only where it still needs recording, else only its length.
                        detail = $"addon#{index} {HousingYesnoMatcher.PromptLogDetail(prompt, language, context.Value)}";
                        continue;
                }
            }

            // Debug only, and only when the prompt (or its length) changed, so it can't alternate with another line
            // every tick (07-A10).
            if (detail != "no addon" && !string.Equals(detail, _lastYesnoIgnoredDetail, StringComparison.Ordinal))
            {
                _lastYesnoIgnoredDetail = detail;
                DebugCall($"yesno: not confirmed ({detail})", key: "yesno-ignored", throttle: false);
            }

            return false;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "House entrance SelectYesno failed");
            detail = $"exception: {ex.GetType().Name}";
            return false;
        }
    }

    /// <summary>
    /// 07-A7: the prompt's plain text from the typed <see cref="AddonSelectYesno.PromptText"/> node, with Lifestream's
    /// <c>NodeList[15]</c> as the second choice. AtkValues are never read: a value's string pointer is only valid for a
    /// string type, and reading it from another type crashed the game.
    /// </summary>
    private static unsafe string ReadSelectYesnoPrompt(AtkUnitBase* addon)
    {
        try
        {
            var textNode = ((AddonSelectYesno*)addon)->PromptText;
            if (textNode is null
                && addon->UldManager.NodeList is not null
                && addon->UldManager.NodeListCount > 15
                && addon->UldManager.NodeList[15] is not null)
                textNode = addon->UldManager.NodeList[15]->GetAsAtkTextNode();
            if (textNode is null)
                return string.Empty;

            // The SeString's text only: macros and payloads (colours, links) are dropped.
            return SeString.Parse(textNode->NodeText.AsSpan()).TextValue;
        }
        catch
        {
            return string.Empty;
        }
    }

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
                _houseEntranceDest = dest.Value;
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

    private string PresenceRequiredEcho() =>
        _pending is null
            ? "Your presence is required by your owner."
            : $"Your presence is required by {OwnerLabel(_pending.OwnerKey)}.";

    /// <summary>The owner's local label (nickname, title, Name@World), or "your owner"; never payload text.</summary>
    private string OwnerLabel(string ownerKey)
    {
        var label = _config.FindPairByKey(ownerKey)?.GetMessageLabel();
        if (string.IsNullOrWhiteSpace(label) || string.Equals(label, "DEBUGCALL", StringComparison.Ordinal))
            label = "your owner";
        return label;
    }

    /// <summary>
    /// 07-B4 (D13): where the Call goes, for the prompt: the zone and world, or for housing the district, ward and the
    /// plot or apartment. Names come from Lumina (by the Call's ids), numbers from the validated Call; no payload text.
    /// </summary>
    private string DescribeDestination(PendingCall call)
    {
        var world = ResolveWorldName(call.WorldId);
        string place;
        if (call.IsHousingCall)
        {
            var district = HousingCallLocation.TryGetResidentialTerritoryForCity(call.HousingCity) is { } districtId
                ? TerritoryName(districtId)
                : string.Empty;
            place = $"{(string.IsNullOrEmpty(district) ? "Residential district" : district)}, Ward {call.HousingWard}";
            if (call.HousingDivision == 2)
                place += " (Subdivision)";
            if (call.HousingIsApartment)
                place += call.HousingApartment > 0 ? $", Apartment {call.HousingApartment}" : ", Apartments";
            else if (call.HousingPlot > 0)
                // An outdoor Call's plot is only the nearest one (FindNearestHousingPlot).
                place += call.HousingIndoor ? $", Plot {call.HousingPlot}" : $", near Plot {call.HousingPlot}";
        }
        else
        {
            place = TerritoryName(call.TerritoryId);
            if (string.IsNullOrEmpty(place))
                place = "Unknown zone";
        }

        return string.IsNullOrEmpty(world) ? place : $"{place} ({world})";
    }

    private string TerritoryName(uint territoryId)
    {
        try
        {
            if (_data.GetExcelSheet<TerritoryType>()?.TryGetRow(territoryId, out var row) == true)
                return row.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
        }
        catch
        {
            // ignore
        }

        return string.Empty;
    }

    /// <summary>
    /// 07-B3: the owner's territory from her party list, or null when they aren't in it. A member's ContentId maps to a
    /// pairing key through <see cref="PairingKeyUtil.FromContentId"/>; neither is ever logged (04-D10).
    /// </summary>
    private CallPlace? OwnerPartyPlace(string ownerKey)
    {
        try
        {
            foreach (var member in _party)
            {
                if (member.ContentId == 0
                    || !string.Equals(PairingKeyUtil.FromContentId((ulong)member.ContentId), ownerKey, StringComparison.Ordinal))
                    continue;

                return PlaceOf(member.Territory.RowId);
            }
        }
        catch (Exception ex)
        {
            _log.Debug("Call party cross-check failed: {Type}", ex.GetType().Name);
        }

        return null;
    }

    /// <summary>
    /// 07-B3: the owner in her object table (so in her territory), or null when she can't see them. As for the party,
    /// the ContentIds and keys are never logged (04-D10).
    /// </summary>
    private unsafe CallOwnerSighting? OwnerSighting(string ownerKey)
    {
        try
        {
            foreach (var obj in _objects.PlayerObjects)
            {
                if (obj.Address == nint.Zero)
                    continue;

                var contentId = ((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)obj.Address)->ContentId;
                if (contentId == 0
                    || !string.Equals(PairingKeyUtil.FromContentId(contentId), ownerKey, StringComparison.Ordinal))
                    continue;

                return new CallOwnerSighting(obj.Position, PlaceOf(_clientState.TerritoryType));
            }
        }
        catch (Exception ex)
        {
            _log.Debug("Call sight cross-check failed: {Type}", ex.GetType().Name);
        }

        return null;
    }

    /// <summary>
    /// 07-B3: a territory for <see cref="CallCorroboration"/>: a residential district by its city, a housing interior
    /// (<c>TerritoryIntendedUse</c> HousingIndoor) by its <c>PlaceNameRegion</c>'s city, anywhere else no city. 07-B6:
    /// both are marked housing even when the interior's city doesn't resolve (an apartment lobby or interior outside the
    /// known region set), which the cross-check then treats as nothing to check, not as a mismatch.
    /// </summary>
    private CallPlace PlaceOf(uint territoryId)
    {
        if (territoryId == 0)
            return default;

        if (HousingCallLocation.TryGetCityForTerritory(territoryId) is { } district)
            return new CallPlace(territoryId, district, IsHousing: true);

        if (_data.GetExcelSheet<TerritoryType>()?.TryGetRow(territoryId, out var row) == true
            && row.TerritoryIntendedUse.RowId == HousingIndoorIntendedUse)
        {
            var city = HousingCallLocation.TryGetCityByPlaceNameRegion(row.PlaceNameRegion.RowId) ?? 0;
            return new CallPlace(territoryId, city, IsHousing: true);
        }

        return new CallPlace(territoryId, 0);
    }

    /// <summary>
    /// The generic chat line, a forced log line with the location-free <paramref name="reason"/> (07-A8), and the
    /// location only in a debug line.
    /// </summary>
    private void ReportEdgeCaseError(string reason, string? locationDetail = null, string echo = CallErrorEcho)
    {
        PluginChat.Print(_chat, echo, PluginChat.Yellow);
        DebugCall(CallLogRules.Forced($"ERROR: {reason}", _phase, PendingKind()), throttle: false, force: true);
        if (locationDetail is not null)
            DebugCall($"ERROR at: {locationDetail}", throttle: false);
    }

    /// <summary>
    /// Append to config-dir CallTravel.debug.log. Routine diagnostics require debug mode; a failure's reason is forced
    /// so the generic player-facing echo always has a report. 07-A8: a forced line is built by
    /// <see cref="CallLogRules.Forced"/> and never carries a location or an exception message.
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

    /// <summary>07-A5: the Lumina sheets behind <see cref="CallRequestRules.Validate"/>. Framework thread.</summary>
    private sealed class LuminaCallSheets(IDataManager data) : ICallSheetFacts
    {
        public bool TryGetWorld(uint worldId, out CallWorldFacts world)
        {
            world = default;
            if (data.GetExcelSheet<World>()?.TryGetRow(worldId, out var row) != true)
                return false;

            world = new CallWorldFacts(row.IsPublic, row.DataCenter.RowId);
            return true;
        }

        public bool TryGetTerritory(uint territoryId, out CallTerritoryFacts territory)
        {
            territory = default;
            if (data.GetExcelSheet<TerritoryType>()?.TryGetRow(territoryId, out var row) != true)
                return false;

            var map = row.Map.ValueNullable;
            territory = new CallTerritoryFacts(
                row.ContentFinderCondition.RowId,
                row.QuestBattle.RowId,
                map?.SizeFactor ?? 0,
                map?.OffsetX ?? 0,
                map?.OffsetY ?? 0);
            return true;
        }

        public bool HasAetheryte(uint territoryId)
        {
            var sheet = data.GetExcelSheet<Aetheryte>();
            if (sheet is null)
                return false;

            foreach (var row in sheet)
            {
                if (row.IsAetheryte && row.Territory.RowId == territoryId)
                    return true;
            }

            return false;
        }
    }
}
