#if WINDUP_TESTING
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using WindUpKey.Protocol;
using WindUpKey.Ui;

namespace WindUpKey.Services;

/// <summary>
/// Testing-only: answers an owner call by traveling near the owner's position via Lifestream + vnavmesh.
/// </summary>
public sealed class CallTravelService : IDisposable
{
    private const float CloseRangeYalms = 5f;
    private const float ArrivedSlopYalms = 1.5f;
    private static readonly TimeSpan TravelStepTimeout = TimeSpan.FromMinutes(3);

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
    private ICallGateSubscriber<(string, int, int, int, int, int, int, bool, bool, string), object>? _lsGoToHousing;
    private ICallGateSubscriber<uint, bool>? _lsHousingAethernetById;
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
    private bool _localDebugCall;
    private CallPayload? _debugStoredPoint;
    private DateTimeOffset _stepStartedUtc;
    private TravelPhase _phase = TravelPhase.Idle;

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

        if (HousingCallLocation.TryRead(territoryId, territoryRow: null, out var housing))
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
            await SendResultOrLocalAsync(payload, CallResultStatuses.Failed, "Already answering another call.", localDebug)
                .ConfigureAwait(false);
            return;
        }

        if (!IsTravelReady)
        {
            await SendResultOrLocalAsync(payload, CallResultStatuses.Failed, "Lifestream and vnavmesh are required.", localDebug)
                .ConfigureAwait(false);
            return;
        }

        if (!SameDataCenter(payload.WorldId))
        {
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
            HousingDivision = payload.HousingDivision,
            HousingPlot = payload.HousingPlot,
            HousingApartment = payload.HousingApartment,
            HousingIsApartment = payload.HousingIsApartment,
            HousingIndoor = payload.HousingIndoor,
        };
        _craftNotified = false;
        _subdivisionHopAttempted = false;
        _weOwnTravel = false;
        _phase = TravelPhase.WaitingGates;
        // Prior cancel/fail must not leave Lifestream mid-task or Accept stays dead.
        AbortCallAutomation();
        _timer.SetCallTravelBypass(true);

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
            if (!_clientState.IsLoggedIn || _objects.LocalPlayer is null)
            {
                _ = FinishAsync(CallResultStatuses.Cancelled, "Logged out.");
                return;
            }

            switch (_phase)
            {
                case TravelPhase.WaitingGates:
                    TickWaitingGates();
                    break;
                case TravelPhase.WaitingAccept:
                    TickWaitingAccept();
                    break;
                case TravelPhase.ChangingWorld:
                    _timer.SetCallTravelMuteDestination(_pending.Position);
                    TickChangingWorld();
                    break;
                case TravelPhase.Teleporting:
                    _timer.SetCallTravelMuteDestination(_pending.Position);
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
            EnterFailedPrompt();
        }
    }

    private void TickWaitingGates()
    {
        if (IsCrafting())
        {
            if (!_craftNotified)
            {
                _craftNotified = true;
                PluginChat.Print(_chat, "Your presence is requested.", PluginChat.Yellow);
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
            CallPromptReason.Combat => !IsInCombat() && !IsExternallyBusy(),
            CallPromptReason.Busy => !IsExternallyBusy() && !IsInCombat(),
            // Failed retries abort leftover automation on Accept — don't keep the button locked
            // because Lifestream is still draining from the attempt that just failed.
            CallPromptReason.Failed => !IsInCombat() && IsTravelReady,
            _ => !IsInCombat() && !IsExternallyBusy(),
        };

        _prompt.CanAccept = clear;
        if (!_prompt.IsOpen)
            _prompt.IsOpen = true;
    }

    private void OnPromptAccept()
    {
        if (_pending is null || !_prompt.CanAccept)
            return;

        _prompt.IsOpen = false;
        AbortCallAutomation();

        if (IsCrafting() || IsInInstance() || IsInCombat())
        {
            _phase = TravelPhase.WaitingGates;
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
        _ = AckAsync(CallAckStatuses.Traveling);
        _timer.SetCallTravelInputMute(true, _pending.Position);

        var localWorld = _objects.LocalPlayer?.CurrentWorld.RowId ?? 0;
        if (localWorld != 0 && localWorld != _pending.WorldId)
        {
            if (!TryChangeWorld(_pending))
            {
                EnterFailedPrompt();
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

        if (IsAtCallDestination())
        {
            if (_pending.IsHousingCall && _pending.HousingIndoor)
            {
                _ = FinishAsync(CallResultStatuses.Arrived, "Arrived near owner.");
                return;
            }

            if (!TryStartPath(_pending.Position))
            {
                EnterFailedPrompt();
                return;
            }

            _weOwnTravel = true;
            _phase = TravelPhase.Pathing;
            _stepStartedUtc = DateTimeOffset.UtcNow;
            return;
        }

        if (_pending.IsHousingCall)
        {
            if (!TryGoToHousing(_pending))
            {
                EnterFailedPrompt();
                return;
            }
        }
        else if (!TryTeleportNear(_pending.TerritoryId, _pending.Position))
        {
            EnterFailedPrompt();
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

        if (DateTimeOffset.UtcNow - _stepStartedUtc > TravelStepTimeout)
        {
            EnterFailedPrompt();
            return;
        }

        if (IsBetweenAreas() || IsLifestreamBusy())
            return;

        if (_pending.IsHousingCall)
        {
            if (!IsAtHousingWard())
            {
                TryHopToSubdivisionIfNeeded();
                return;
            }

            if (_pending.HousingIndoor)
            {
                _ = FinishAsync(CallResultStatuses.Arrived, "Arrived near owner.");
                return;
            }
        }
        else if (_clientState.TerritoryType != _pending.TerritoryId)
        {
            return;
        }

        if (!TryStartPath(_pending.Position))
        {
            EnterFailedPrompt();
            return;
        }

        _phase = TravelPhase.Pathing;
        _stepStartedUtc = DateTimeOffset.UtcNow;
    }

    private void TickPathing()
    {
        if (_pending is null)
            return;

        if (DateTimeOffset.UtcNow - _stepStartedUtc > TravelStepTimeout)
        {
            StopOurPath();
            EnterFailedPrompt();
            return;
        }

        // Left the correct housing instance mid-path — stop rather than wall-running.
        if (_pending.IsHousingCall && !IsAtHousingWard())
        {
            StopOurPath();
            EnterFailedPrompt();
            return;
        }

        var player = _objects.LocalPlayer;
        if (player is null)
            return;

        var dist = Vector3.Distance(player.Position, _pending.Position);
        if (dist <= CloseRangeYalms + ArrivedSlopYalms)
        {
            StopOurPath();
            _ = FinishAsync(CallResultStatuses.Arrived, "Arrived near owner.");
            return;
        }

        if (!IsVnavBusy() && dist > CloseRangeYalms + ArrivedSlopYalms)
        {
            // Path ended far away — treat as failure for Accept retry.
            EnterFailedPrompt();
        }
    }

    private void EnterFailedPrompt()
    {
        if (_pending is null)
            return;

        AbortCallAutomation();
        _timer.SetCallTravelInputMute(false);
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
                PluginChat.Print(_chat, $"Debug call recall failed: {message}", PluginChat.Yellow);
            return;
        }

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
            PluginChat.Print(_chat, $"Debug call recall failed: {message}", PluginChat.Yellow);
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
        _timer.SetCallTravelBypass(false);
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
            _lsGoToHousing = _pi.GetIpcSubscriber<(string, int, int, int, int, int, int, bool, bool, string), object>("Lifestream.GoToHousingAddress");
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
        if (_lsGoToHousing is null)
            return false;

        var plot = call.HousingPlot > 0 ? call.HousingPlot : 1;
        var apartment = call.HousingApartment > 0 ? call.HousingApartment : 1;
        var isSub = call.HousingDivision == 2;
        var propertyType = call.HousingIsApartment ? 1 : 0; // House=0, Apartment=1
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

        try
        {
            _lsGoToHousing.InvokeAction(entry);
            return true;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Lifestream GoToHousingAddress failed");
            return false;
        }
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
            if (_vnavReady?.InvokeFunc() != true)
                return false;
            return _vnavMoveCloseTo?.InvokeFunc(dest, false, CloseRangeYalms) ?? false;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "vnavmesh PathfindAndMoveCloseTo failed");
            return false;
        }
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
            return IsAtHousingWard();
        return _clientState.TerritoryType == _pending.TerritoryId;
    }

    private unsafe bool IsAtHousingWard()
    {
        if (_pending is null || !_pending.IsHousingCall)
            return false;

        var expectedTerritory = HousingCallLocation.TryGetResidentialTerritoryForCity(_pending.HousingCity)
                                ?? _pending.TerritoryId;
        if (_clientState.TerritoryType != expectedTerritory)
            return false;

        var h = HousingManager.Instance();
        if (h is null)
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

        return division == _pending.HousingDivision;
    }

    private unsafe void TryHopToSubdivisionIfNeeded()
    {
        if (_pending is null || _subdivisionHopAttempted || !_pending.IsHousingCall)
            return;
        if (_pending.HousingDivision != 2)
            return;

        var h = HousingManager.Instance();
        if (h is null)
            return;

        var ward = h->GetCurrentWard() + 1;
        if (ward != _pending.HousingWard)
            return;
        if (h->GetCurrentDivision() == 2)
            return;

        if (!SubdivisionAetheryteByCity.TryGetValue(_pending.HousingCity, out var aetheryteId))
            return;

        _subdivisionHopAttempted = true;
        try
        {
            if (_lsHousingAethernetById?.InvokeFunc(aetheryteId) == true)
                _stepStartedUtc = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Lifestream HousingAethernetTeleportById failed");
        }
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
