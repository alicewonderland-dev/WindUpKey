#if WINDUP_TESTING
using System;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
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
    private ICallGateSubscriber<uint, bool>? _lsChangeWorldById;
    private ICallGateSubscriber<string, bool>? _lsChangeWorld;
    private ICallGateSubscriber<uint, byte, bool>? _lsTeleport;
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

    public async Task RequestAsync(CallPayload payload)
    {
        if (_disposed || payload is null)
            return;

        if (_pending is not null)
        {
            await _sendResult(new CallResultPayload
            {
                RequestId = payload.RequestId,
                From = _config.PairingKey,
                To = PairingKeyUtil.Normalize(payload.From),
                Status = CallResultStatuses.Failed,
                Message = "Already answering another call.",
            }).ConfigureAwait(false);
            return;
        }

        if (!IsTravelReady)
        {
            await _sendResult(new CallResultPayload
            {
                RequestId = payload.RequestId,
                From = _config.PairingKey,
                To = PairingKeyUtil.Normalize(payload.From),
                Status = CallResultStatuses.Failed,
                Message = "Lifestream and vnavmesh are required.",
            }).ConfigureAwait(false);
            return;
        }

        if (!SameDataCenter(payload.WorldId))
        {
            await _sendResult(new CallResultPayload
            {
                RequestId = payload.RequestId,
                From = _config.PairingKey,
                To = PairingKeyUtil.Normalize(payload.From),
                Status = CallResultStatuses.Failed,
                Message = "Cannot travel across data centers.",
            }).ConfigureAwait(false);
            return;
        }

        _pending = new PendingCall
        {
            RequestId = payload.RequestId,
            OwnerKey = PairingKeyUtil.Normalize(payload.From),
            WorldId = payload.WorldId,
            WorldName = payload.WorldName?.Trim() ?? string.Empty,
            TerritoryId = payload.TerritoryId,
            Position = new Vector3(payload.X, payload.Y, payload.Z),
        };
        _craftNotified = false;
        _weOwnTravel = false;
        _phase = TravelPhase.WaitingGates;
        _timer.SetCallTravelBypass(true);

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
            CallPromptReason.Failed => !IsInCombat() && !IsExternallyBusy() && IsTravelReady,
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
        if (IsCrafting() || IsInInstance() || IsInCombat() || IsExternallyBusy())
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

        var territory = _clientState.TerritoryType;
        if (territory == _pending.TerritoryId)
        {
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

        if (!TryTeleportNear(_pending.TerritoryId, _pending.Position))
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

        if (_clientState.TerritoryType != _pending.TerritoryId)
            return;

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

        StopOurPath();
        _weOwnTravel = false;
        _timer.SetCallTravelInputMute(false);
        ShowPrompt(CallPromptReason.Failed, canAccept: !IsInCombat() && !IsExternallyBusy());
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
        CancelInternal(sendResult: false);
        if (pending is null)
            return;

        await _sendResult(new CallResultPayload
        {
            RequestId = pending.RequestId,
            From = _config.PairingKey,
            To = pending.OwnerKey,
            Status = status,
            Message = message,
        }).ConfigureAwait(false);
    }

    private void CancelInternal(bool sendResult)
    {
        StopOurPath();
        _weOwnTravel = false;
        _phase = TravelPhase.Idle;
        _prompt.IsOpen = false;
        _timer.SetCallTravelBypass(false);
        _timer.SetCallTravelInputMute(false);
        _pending = null;
        _ = sendResult;
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
            _lsChangeWorldById = _pi.GetIpcSubscriber<uint, bool>("Lifestream.ChangeWorldById");
            _lsChangeWorld = _pi.GetIpcSubscriber<string, bool>("Lifestream.ChangeWorld");
            _lsTeleport = _pi.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport");
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

    private bool TryFindNearestAetheryte(uint territoryId, Vector3 dest, out uint aetheryteId)
    {
        aetheryteId = 0;
        _ = dest;
        var sheet = _data.GetExcelSheet<Aetheryte>();
        if (sheet is null)
            return false;

        foreach (var row in sheet)
        {
            if (!row.IsAetheryte)
                continue;
            if (row.Territory.RowId != territoryId)
                continue;
            aetheryteId = row.RowId;
            return aetheryteId != 0;
        }

        return false;
    }
}
#endif
